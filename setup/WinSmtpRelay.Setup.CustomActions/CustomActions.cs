using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using WixToolset.Dtf.WindowsInstaller;

namespace WinSmtpRelay.Setup.CustomActions
{
    public static class CustomActions
    {
        // Admin-UI port fallbacks tried in order when 8025 is already in use.
        private static readonly int[] AdminPortCandidates = { 8025, 8125, 8225, 8325, 8425, 8525, 8625, 8725, 8825, 8925 };

        /// <summary>
        /// Immediate CA (runs in the UI and execute sequences before the config is written). Detects
        /// whether SMTP port 25 and the admin-UI port 8025 are already in use, picks a free admin port,
        /// and publishes ADMINUIPORT / ADMINUIURL / PORT25INUSE plus a ready-made exit-dialog message.
        /// </summary>
        [CustomAction]
        public static ActionResult CheckPorts(Session session)
        {
            try
            {
                var used = GetUsedTcpPorts();

                bool port25InUse = used.Contains(25);
                session["PORT25INUSE"] = port25InUse ? "1" : "0";

                int adminPort = AdminPortCandidates.FirstOrDefault(p => !used.Contains(p));
                if (adminPort == 0)
                {
                    // Every preferred candidate is taken — scan upward as a last resort.
                    adminPort = Enumerable.Range(8025, 2000).FirstOrDefault(p => !used.Contains(p));
                    if (adminPort == 0) adminPort = 8025; // give up gracefully; the service will report a bind error
                }

                bool adminReassigned = adminPort != 8025;

                session["ADMINUIPORT"] = adminPort.ToString();
                session["ADMINUIURL"] = "https://localhost:" + adminPort;

                // Build the exit-dialog text dynamically so the operator sees any port issues at the end.
                string text = "WIN-SMTP-RELAY is installed. Open the admin UI at https://localhost:" + adminPort
                    + " (also added to the Start menu). The built-in certificate is self-signed, so your browser "
                    + "will warn once — you can import a trusted certificate later in the admin UI."
                    + "  Sign in as admin@local — the one-time initial password is in "
                    + "'initial-admin-password.txt' in the install folder (also in the Windows Event Log, "
                    + "source WinSmtpRelay.Service).";
                if (adminReassigned)
                    text = "Port 8025 was already in use, so the admin UI was moved to port " + adminPort + ". " + text;
                if (port25InUse)
                    text += "  WARNING: TCP port 25 is already in use by another program, so the relay cannot "
                        + "receive mail on port 25 until that is resolved (the admin UI is unaffected).";
                session["WIXUI_EXITDIALOGOPTIONALTEXT"] = text;

                session.Log("WinSmtpRelay CheckPorts: adminPort={0} (reassigned={1}), port25InUse={2}",
                    adminPort, adminReassigned, port25InUse);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay CheckPorts failed, using defaults: " + ex);
                session["ADMINUIPORT"] = "8025";
                session["ADMINUIURL"] = "https://localhost:8025";
                session["PORT25INUSE"] = "0";
                return ActionResult.Success; // never block the install on a detection failure
            }
        }

        /// <summary>
        /// Immediate CA for maintenance (Change / Repair). Reads the existing appsettings.Production.json so
        /// the options dialog reflects the current state and the admin port is preserved — CheckPorts does
        /// not run on maintenance, and the ADMINUIPORT default (8025) would otherwise be wrong if the install
        /// had chosen another port. Sets ADMINUIPORT/ADMINUIURL to the current port, NETWORKACCESS=1 when the
        /// current bind is anything but loopback (so the network checkbox is pre-checked), and WSRMAINTUI=1
        /// to mark that the maintenance UI ran (gates whether WriteAdminConfig re-applies the config).
        /// Port/BindAddress are read from the "AdminUi" section only — other sections may carry keys with
        /// the same names.
        /// </summary>
        [CustomAction]
        public static ActionResult ReadExistingConfig(Session session)
        {
            try
            {
                session["WSRMAINTUI"] = "1";

                var dir = session["INSTALLFOLDER"];
                if (string.IsNullOrEmpty(dir))
                    return ActionResult.Success;

                var path = Path.Combine(dir, "appsettings.Production.json");
                if (!File.Exists(path))
                    return ActionResult.Success;

                var json = File.ReadAllText(path);
                if (!TryGetAdminUiSection(json, out var sectionStart, out var sectionEnd))
                    return ActionResult.Success;
                var section = json.Substring(sectionStart, sectionEnd - sectionStart + 1);

                var port = System.Text.RegularExpressions.Regex.Match(section, "\"Port\"\\s*:\\s*(\\d+)");
                if (port.Success)
                {
                    session["ADMINUIPORT"] = port.Groups[1].Value;
                    // Keep the Start-menu shortcut (and exit text) on the real port — both use ADMINUIURL,
                    // and a Repair rewrites the shortcut with the current property value.
                    session["ADMINUIURL"] = "https://localhost:" + port.Groups[1].Value;
                }

                // Pre-check the network checkbox for ANY non-loopback bind (0.0.0.0 or a specific address),
                // so a Repair doesn't silently revert a hand-configured bind to 127.0.0.1. Leave the
                // property empty for loopback — empty reads as unchecked, whereas "0" would (wrongly) render
                // the checkbox as checked (an MSI checkbox is "checked" whenever its property is non-empty).
                var bind = System.Text.RegularExpressions.Regex.Match(section, "\"BindAddress\"\\s*:\\s*\"([^\"]+)\"");
                if (bind.Success && !IsLoopback(bind.Groups[1].Value))
                    session["NETWORKACCESS"] = "1";

                session.Log("WinSmtpRelay ReadExistingConfig: port={0}, bind={1}",
                    port.Success ? port.Groups[1].Value : "(default)",
                    bind.Success ? bind.Groups[1].Value : "(none)");
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay ReadExistingConfig failed: " + ex);
                return ActionResult.Success;
            }
        }

        /// <summary>
        /// Deferred CA. Patches appsettings.Production.json next to the service binaries with the chosen
        /// admin-UI port and bind address. The service is installed to run in the Production environment
        /// (ServiceInstall Arguments="--environment Production"), so ASP.NET Core layers this file over the
        /// shipped appsettings.json via the standard appsettings.{Environment}.json convention — no custom
        /// loading and no parsing of appsettings.json. Data is passed via CustomActionData as
        /// "DIR=&lt;installdir&gt;|PORT=&lt;port&gt;|NET=&lt;0|1&gt;".
        /// MERGES into an existing file: only the AdminUi Port/BindAddress values are touched, every other
        /// operator-added setting in the file survives a Repair or upgrade byte-for-byte. A hand-configured
        /// specific bind address (e.g. 192.168.1.5) is preserved when network access stays enabled.
        /// Hand-rolled string patching keeps this net48 CA dependency-free (no JSON library).
        /// </summary>
        [CustomAction]
        public static ActionResult WriteAdminConfig(Session session)
        {
            try
            {
                var data = ParseCustomActionData(session["CustomActionData"]);
                string dir = data.TryGetValue("DIR", out var d) ? d : null;
                string port = data.TryGetValue("PORT", out var p) ? p : "8025";
                // NET=1 → expose on the network; otherwise loopback only (secure default).
                string net = data.TryGetValue("NET", out var n) ? n : "0";

                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    session.Log("WinSmtpRelay WriteAdminConfig: install directory '{0}' not found — skipping.", dir);
                    return ActionResult.Success;
                }

                if (!int.TryParse(port, out var portNum)) portNum = 8025;

                string path = Path.Combine(dir, "appsettings.Production.json");
                string existing = File.Exists(path) ? File.ReadAllText(path) : null;

                // Network ON keeps a hand-configured specific bind address; only loopback (or no value)
                // is widened to all interfaces. Network OFF always means loopback.
                string bind = "127.0.0.1";
                if (net == "1")
                {
                    bind = "0.0.0.0";
                    if (existing != null && TryGetAdminUiSection(existing, out var s0, out var e0))
                    {
                        var current = System.Text.RegularExpressions.Regex.Match(
                            existing.Substring(s0, e0 - s0 + 1), "\"BindAddress\"\\s*:\\s*\"([^\"]+)\"");
                        if (current.Success && !IsLoopback(current.Groups[1].Value))
                            bind = current.Groups[1].Value;
                    }
                }

                string json;
                if (existing != null && TryGetAdminUiSection(existing, out var sectionStart, out var sectionEnd))
                {
                    var section = existing.Substring(sectionStart, sectionEnd - sectionStart + 1);
                    section = SetJsonValueInSection(section, "Port", portNum.ToString());
                    section = SetJsonValueInSection(section, "BindAddress", "\"" + bind + "\"");
                    json = existing.Substring(0, sectionStart) + section + existing.Substring(sectionEnd + 1);
                }
                else if (existing != null && existing.IndexOf('{') >= 0)
                {
                    // File exists but has no AdminUi section — insert one, keep the rest untouched.
                    int root = existing.IndexOf('{');
                    bool empty = existing.Substring(root + 1).TrimStart().StartsWith("}");
                    json = existing.Substring(0, root + 1) +
                           "\r\n  \"AdminUi\": {\r\n" +
                           "    \"Port\": " + portNum + ",\r\n" +
                           "    \"BindAddress\": \"" + bind + "\"\r\n" +
                           "  }" + (empty ? "\r\n" : ",") + existing.Substring(root + 1);
                }
                else
                {
                    // No (usable) file yet — write the minimal override.
                    json =
                        "{\r\n" +
                        "  \"AdminUi\": {\r\n" +
                        "    \"Port\": " + portNum + ",\r\n" +
                        "    \"BindAddress\": \"" + bind + "\"\r\n" +
                        "  }\r\n" +
                        "}\r\n";
                }

                File.WriteAllText(path, json);
                session.Log("WinSmtpRelay WriteAdminConfig: wrote {0} (Port={1}, BindAddress={2}, merged={3})",
                    path, portNum, bind, existing != null);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay WriteAdminConfig failed: " + ex);
                // Don't fail the install: the service still runs on the appsettings.json defaults.
                return ActionResult.Success;
            }
        }

        /// <summary>
        /// Locates the object value of the top-level "AdminUi" property: start/end are the indexes of its
        /// opening and closing braces. Brace counting skips string literals, so values containing braces
        /// can't derail it. Returns false when the section (or the file's JSON shape) is absent.
        /// </summary>
        private static bool TryGetAdminUiSection(string json, out int braceStart, out int braceEnd)
        {
            braceStart = braceEnd = -1;
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"AdminUi\"\\s*:\\s*\\{");
            if (!m.Success) return false;

            braceStart = m.Index + m.Length - 1;
            int depth = 0;
            bool inString = false;
            for (int i = braceStart; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\') i++;        // skip the escaped character
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    braceEnd = i;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Replaces the value of <paramref name="key"/> inside a JSON object snippet ("{...}"), or inserts
        /// the key after the opening brace when absent. <paramref name="rawValue"/> is written verbatim
        /// (pass quoted strings pre-quoted).
        /// </summary>
        private static string SetJsonValueInSection(string section, string key, string rawValue)
        {
            var rx = new System.Text.RegularExpressions.Regex(
                "(\"" + key + "\"\\s*:\\s*)(\"(?:[^\"\\\\]|\\\\.)*\"|[^,\\}\\s]+)");
            if (rx.IsMatch(section))
                return rx.Replace(section, mm => mm.Groups[1].Value + rawValue, 1);

            int brace = section.IndexOf('{');
            bool empty = section.Substring(brace + 1).TrimStart().StartsWith("}");
            return section.Substring(0, brace + 1) +
                   "\r\n    \"" + key + "\": " + rawValue + (empty ? "\r\n  " : ",") +
                   section.Substring(brace + 1);
        }

        private static bool IsLoopback(string bindAddress) =>
            bindAddress == "127.0.0.1" || bindAddress == "localhost" || bindAddress == "::1";

        private static HashSet<int> GetUsedTcpPorts()
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            var ports = new HashSet<int>();
            foreach (var ep in props.GetActiveTcpListeners())
                ports.Add(ep.Port);
            // Active outbound connections' local ports don't block a listener bind, so only listeners matter.
            return ports;
        }

        private static Dictionary<string, string> ParseCustomActionData(string raw)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw)) return result;
            foreach (var pair in raw.Split('|'))
            {
                var idx = pair.IndexOf('=');
                if (idx > 0)
                    result[pair.Substring(0, idx)] = pair.Substring(idx + 1);
            }
            return result;
        }
    }
}
