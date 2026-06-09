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
        /// had chosen another port. Sets ADMINUIPORT to the current port, NETWORKACCESS=1 when the current
        /// bind is 0.0.0.0 (so the network checkbox is pre-checked), and WSRMAINTUI=1 to mark that the
        /// maintenance UI ran (gates whether WriteAdminConfig re-applies the config on a repair).
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

                var port = System.Text.RegularExpressions.Regex.Match(json, "\"Port\"\\s*:\\s*(\\d+)");
                if (port.Success)
                    session["ADMINUIPORT"] = port.Groups[1].Value;

                // Pre-check the network checkbox only when currently bound to all interfaces. Leave the
                // property empty for loopback — empty reads as unchecked, whereas "0" would (wrongly) render
                // the checkbox as checked (an MSI checkbox is "checked" whenever its property is non-empty).
                var bind = System.Text.RegularExpressions.Regex.Match(json, "\"BindAddress\"\\s*:\\s*\"([^\"]+)\"");
                if (bind.Success && bind.Groups[1].Value == "0.0.0.0")
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
        /// Deferred CA. Writes appsettings.Production.json next to the service binaries with the chosen
        /// admin-UI port and bind address. The service is installed to run in the Production environment
        /// (ServiceInstall Arguments="--environment Production"), so ASP.NET Core layers this file over the
        /// shipped appsettings.json via the standard appsettings.{Environment}.json convention — no custom
        /// loading and no parsing of appsettings.json. Data is passed via CustomActionData as
        /// "DIR=&lt;installdir&gt;|PORT=&lt;port&gt;|BIND=&lt;address&gt;".
        /// </summary>
        [CustomAction]
        public static ActionResult WriteAdminConfig(Session session)
        {
            try
            {
                var data = ParseCustomActionData(session["CustomActionData"]);
                string dir = data.TryGetValue("DIR", out var d) ? d : null;
                string port = data.TryGetValue("PORT", out var p) ? p : "8025";
                // NET=1 → expose on all interfaces; otherwise loopback only (secure default).
                string net = data.TryGetValue("NET", out var n) ? n : "0";
                string bind = net == "1" ? "0.0.0.0" : "127.0.0.1";

                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    session.Log("WinSmtpRelay WriteAdminConfig: install directory '{0}' not found — skipping.", dir);
                    return ActionResult.Success;
                }

                if (!int.TryParse(port, out var portNum)) portNum = 8025;

                // Minimal override file — only the keys the installer decides. Everything else stays in
                // the shipped appsettings.json. Hand-written JSON keeps this CA dependency-free.
                string json =
                    "{\r\n" +
                    "  \"AdminUi\": {\r\n" +
                    "    \"Port\": " + portNum + ",\r\n" +
                    "    \"BindAddress\": \"" + bind + "\"\r\n" +
                    "  }\r\n" +
                    "}\r\n";

                string path = Path.Combine(dir, "appsettings.Production.json");
                File.WriteAllText(path, json);
                session.Log("WinSmtpRelay WriteAdminConfig: wrote {0} (Port={1}, BindAddress={2})", path, portNum, bind);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay WriteAdminConfig failed: " + ex);
                // Don't fail the install: the service still runs on the appsettings.json defaults.
                return ActionResult.Success;
            }
        }

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
