using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using WinSmtpRelay.DataLocation;
using WixToolset.Dtf.WindowsInstaller;

namespace WinSmtpRelay.Setup.CustomActions
{
    public static class CustomActions
    {
        // Admin-UI port fallbacks tried in order when 8025 is already in use.
        private static readonly int[] AdminPortCandidates = { 8025, 8125, 8225, 8325, 8425, 8525, 8625, 8725, 8825, 8925 };

        private const string AdminUiReadyText =
            " (also added to the Start menu). The built-in certificate is self-signed, so your browser will warn once"
            + " - you can import a trusted certificate later in the admin UI. On the first visit the admin UI asks you to"
            + " create the first administrator. Configuration and data are kept in %ProgramData%\\Ardimedia\\WinSmtpRelay.";

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
                string text = "WIN-SMTP-RELAY is installed. Open the admin UI at https://localhost:" + adminPort + AdminUiReadyText;
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
        /// Immediate CA in both sequences, after every source of ADMINUIPORT (command line, the saved registry value,
        /// CheckPorts, ReadExistingConfig): refuses the installation when it is not a port number 1-65535. The value
        /// ends up in a firewall rule and in the admin binding, so it is never passed on unchecked.
        /// </summary>
        [CustomAction]
        public static ActionResult ValidateAdminPort(Session session)
        {
            var value = session["ADMINUIPORT"];
            if (InstallerInput.TryParsePort(value, out _))
                return ActionResult.Success;

            var message = "ADMINUIPORT must be a TCP port number between 1 and 65535 (got '" + value + "'). Installation stopped.";
            session.Log("WinSmtpRelay ValidateAdminPort: " + message);
            using (var record = new Record(0))
            {
                record.FormatString = message;
                session.Message(InstallMessage.Error | (InstallMessage)MessageButtons.OK, record);
            }
            return ActionResult.Failure;
        }

        /// <summary>
        /// Immediate CA, scheduled (UI and execute sequence) on an upgrade or maintenance run when the installer's
        /// registry key records no network choice for the admin UI — the first upgrade from a version that kept it in
        /// the service's key, a pre-build45 install, or a build80 install that lost the choice that way. Reads the
        /// existing appsettings.Production.json (data folder first, then the legacy program folder) so the options
        /// dialog reflects the current state and the admin port is preserved. Sets ADMINUIPORT/ADMINUIURL to the current
        /// port and NETWORKACCESS=1 when the current bind is anything but loopback. Port/BindAddress are read from the
        /// "AdminUi" section only — other sections may carry keys with the same names. Invalid ports are ignored.
        /// Runs BEFORE CostInitialize: component conditions (the admin-UI firewall rule) are evaluated during costing,
        /// so NETWORKACCESS set any later reached WriteAdminConfig but not the firewall component, and an upgrade kept
        /// the network bind while removing its firewall rule. Directory properties are not resolved yet at that point,
        /// so the default locations are built from the standard folder properties.
        /// </summary>
        [CustomAction]
        public static ActionResult ReadExistingConfig(Session session)
        {
            try
            {
                string Under(string root, string relative) =>
                    string.IsNullOrEmpty(root) ? null : Path.Combine(root, relative);

                string path = new[]
                    {
                        session["WsrDataFolder"],
                        Under(session["CommonAppDataFolder"], @"Ardimedia\WinSmtpRelay"),
                        session["INSTALLFOLDER"],
                        Under(session["ProgramFiles64Folder"], "WinSmtpRelay"),
                    }
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Select(d => Path.Combine(d, "appsettings.Production.json"))
                    .FirstOrDefault(p => { try { return File.Exists(p); } catch { return false; } });
                if (path == null)
                    return ActionResult.Success;

                var json = File.ReadAllText(path);
                if (!TryGetAdminUiSection(json, out var sectionStart, out var sectionEnd))
                    return ActionResult.Success;
                var section = json.Substring(sectionStart, sectionEnd - sectionStart + 1);

                var port = System.Text.RegularExpressions.Regex.Match(section, "\"Port\"\\s*:\\s*(\\d+)");
                if (port.Success && InstallerInput.TryParsePort(port.Groups[1].Value, out var portNum))
                {
                    session["ADMINUIPORT"] = portNum.ToString();
                    // Keep the Start-menu shortcut (and exit text) on the real port — both use ADMINUIURL,
                    // and a Repair rewrites the shortcut with the current property value.
                    session["ADMINUIURL"] = "https://localhost:" + portNum;
                }

                // Pre-check the network checkbox for ANY non-loopback bind (0.0.0.0 or a specific address),
                // so a Repair doesn't silently revert a hand-configured bind to 127.0.0.1. Leave the
                // property empty for loopback — empty reads as unchecked, whereas "0" would (wrongly) render
                // the checkbox as checked (an MSI checkbox is "checked" whenever its property is non-empty).
                var bind = System.Text.RegularExpressions.Regex.Match(section, "\"BindAddress\"\\s*:\\s*\"([^\"]+)\"");
                if (bind.Success && !IsLoopback(bind.Groups[1].Value))
                    session["NETWORKACCESS"] = "1";

                session.Log("WinSmtpRelay ReadExistingConfig: {0}: port={1}, bind={2}", path,
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
        /// Deferred CA. Patches appsettings.Production.json in the data folder with the chosen admin-UI port and bind
        /// address. The service runs in the Production environment (--environment Production) with the data folder
        /// as its content root, so ASP.NET Core layers this file over appsettings.json by the standard
        /// appsettings.{Environment}.json convention. CustomActionData: "DIR=&lt;datafolder&gt;|PORT=&lt;port&gt;|NET=&lt;0|1&gt;".
        /// MERGES into an existing file: only the AdminUi Port/BindAddress values are touched, every other
        /// operator-added setting in the file survives a Repair or upgrade byte-for-byte. A hand-configured
        /// specific bind address (e.g. 192.168.1.5) is preserved when network access stays enabled.
        /// Hand-rolled string patching keeps this net48 CA dependency-free (no JSON library).
        /// </summary>
        [CustomAction]
        public static ActionResult WriteAdminConfig(Session session)
        {
            Dictionary<string, string> data;
            try
            {
                data = InstallerInput.ParseCustomActionData(session["CustomActionData"], "DIR", "PORT", "NET");
            }
            catch (FormatException ex)
            {
                session.Log("WinSmtpRelay WriteAdminConfig: rejected CustomActionData - " + ex.Message);
                return ActionResult.Failure;
            }

            string dir = data.TryGetValue("DIR", out var d) ? d : null;
            string port = data.TryGetValue("PORT", out var p) ? p : "8025";
            // NET=1 → expose on the network; otherwise loopback only (secure default).
            string net = data.TryGetValue("NET", out var n) ? n : "0";

            if (!InstallerInput.TryParsePort(port, out var portNum))
            {
                session.Log("WinSmtpRelay WriteAdminConfig: rejected admin port '{0}' (must be 1-65535).", port);
                return ActionResult.Failure;
            }

            try
            {
                if (string.IsNullOrEmpty(dir))
                {
                    session.Log("WinSmtpRelay WriteAdminConfig: no data folder passed - skipping.");
                    return ActionResult.Success;
                }
                Directory.CreateDirectory(dir);

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
                // Don't fail the install on an I/O problem: the service still runs on the appsettings.json defaults.
                return ActionResult.Success;
            }
        }

        // ============================================================================================
        // Data folder (decision T1076): move the data of a pre-ProgramData install exactly once.
        // Deferred (as SYSTEM, before InstallFiles), with a rollback action that moves everything back and a
        // commit action that drops the journal. CustomActionData: "LEGACY=<program folder>|DATA=<data folder>".
        // ============================================================================================

        [CustomAction]
        public static ActionResult MigrateLegacyData(Session session)
        {
            try
            {
                var data = InstallerInput.ParseCustomActionData(session["CustomActionData"], "LEGACY", "DATA");
                // Lock the data folders down BEFORE anything is moved into them: a freshly created folder under
                // ProgramData would otherwise inherit read access for every local user.
                ProtectDataFolders(session, Value(data, "DATA"));
                var migrator = new LegacyDataMigrator(m => session.Log("WinSmtpRelay " + m));
                migrator.Migrate(Value(data, "LEGACY"), Value(data, "DATA"));
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                // Fail the installation: the rollback action moves back what was moved. Starting the new version
                // without its data would create an empty database in the data folder.
                session.Log("WinSmtpRelay MigrateLegacyData FAILED: " + ex);
                return ActionResult.Failure;
            }
        }

        [CustomAction]
        public static ActionResult MigrateLegacyDataRollback(Session session)
        {
            try
            {
                var data = InstallerInput.ParseCustomActionData(session["CustomActionData"], "LEGACY", "DATA");
                new LegacyDataMigrator(m => session.Log("WinSmtpRelay " + m)).Rollback(Value(data, "DATA"));
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay MigrateLegacyDataRollback failed: " + ex);
            }
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult MigrateLegacyDataCommit(Session session)
        {
            try
            {
                var data = InstallerInput.ParseCustomActionData(session["CustomActionData"], "LEGACY", "DATA");
                new LegacyDataMigrator(m => session.Log("WinSmtpRelay " + m)).Commit(Value(data, "DATA"));
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay MigrateLegacyDataCommit failed: " + ex);
            }
            return ActionResult.Success;
        }

        // ============================================================================================
        // Folder permissions (decision T1076). Deferred, as SYSTEM, after InstallFiles. Every folder gets an explicit
        // ACL with inheritance broken; a failure fails the installation (an unhardened folder would let the service
        // account write what SYSTEM later trusts). CustomActionData: "PROGRAM=<program folder>|DATA=<data folder>".
        //   program folder        SYSTEM, Administrators: Full   Users, NetworkService: Read & Execute
        //   data folder           SYSTEM, Administrators: Full   NetworkService: Modify
        //   data\updates          SYSTEM, Administrators: Full   NetworkService: Modify (the service downloads here)
        //   data\updater-work     SYSTEM, Administrators: Full   (nobody else)
        // ============================================================================================

        [CustomAction]
        public static ActionResult HardenFolders(Session session)
        {
            try
            {
                var data = InstallerInput.ParseCustomActionData(session["CustomActionData"], "PROGRAM", "DATA");
                string program = Value(data, "PROGRAM"), dataFolder = Value(data, "DATA");

                // Keep the program folder's previous ACL so a rolled-back upgrade gives the restored old version its
                // write access back (it still keeps its database in the program folder).
                ProtectDataFolders(session, dataFolder);
                var backup = Path.Combine(dataFolder, ProgramAclBackupFileName);
                if (Directory.Exists(program) && !File.Exists(backup))
                    File.WriteAllText(backup, Directory.GetAccessControl(program, AccessControlSections.Access)
                        .GetSecurityDescriptorSddlForm(AccessControlSections.Access));

                Protect(session, program,
                    Grant(Sid(WellKnownSidType.LocalSystemSid), FileSystemRights.FullControl),
                    Grant(Sid(WellKnownSidType.BuiltinAdministratorsSid), FileSystemRights.FullControl),
                    Grant(Sid(WellKnownSidType.BuiltinUsersSid), FileSystemRights.ReadAndExecute),
                    Grant(Sid(WellKnownSidType.NetworkServiceSid), FileSystemRights.ReadAndExecute));
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay HardenFolders FAILED: " + ex);
                return ActionResult.Failure;
            }
        }

        private const string ProgramAclBackupFileName = ".program-folder-acl";

        [CustomAction]
        public static ActionResult HardenFoldersRollback(Session session)
        {
            try
            {
                var data = InstallerInput.ParseCustomActionData(session["CustomActionData"], "PROGRAM", "DATA");
                string program = Value(data, "PROGRAM");
                var backup = Path.Combine(Value(data, "DATA"), ProgramAclBackupFileName);
                if (File.Exists(backup) && Directory.Exists(program))
                {
                    var security = new DirectorySecurity();
                    security.SetSecurityDescriptorSddlForm(File.ReadAllText(backup), AccessControlSections.Access);
                    Directory.SetAccessControl(program, security);
                    session.Log("WinSmtpRelay HardenFoldersRollback: restored the previous ACL of {0}.", program);
                }
                if (File.Exists(backup))
                    File.Delete(backup);
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay HardenFoldersRollback failed: " + ex);
            }
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult HardenFoldersCommit(Session session)
        {
            try
            {
                var data = InstallerInput.ParseCustomActionData(session["CustomActionData"], "PROGRAM", "DATA");
                var backup = Path.Combine(Value(data, "DATA"), ProgramAclBackupFileName);
                if (File.Exists(backup))
                    File.Delete(backup);
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay HardenFoldersCommit failed: " + ex);
            }
            return ActionResult.Success;
        }

        private static void ProtectDataFolders(Session session, string dataFolder)
        {
            var system = Sid(WellKnownSidType.LocalSystemSid);
            var admins = Sid(WellKnownSidType.BuiltinAdministratorsSid);
            var networkService = Sid(WellKnownSidType.NetworkServiceSid);
            Protect(session, dataFolder,
                Grant(system, FileSystemRights.FullControl), Grant(admins, FileSystemRights.FullControl),
                Grant(networkService, FileSystemRights.Modify));
            Protect(session, Path.Combine(dataFolder, "updates"),
                Grant(system, FileSystemRights.FullControl), Grant(admins, FileSystemRights.FullControl),
                Grant(networkService, FileSystemRights.Modify));
            Protect(session, Path.Combine(dataFolder, "updater-work"),
                Grant(system, FileSystemRights.FullControl), Grant(admins, FileSystemRights.FullControl));
        }

        private static SecurityIdentifier Sid(WellKnownSidType type) => new SecurityIdentifier(type, null);

        private static FileSystemAccessRule Grant(SecurityIdentifier sid, FileSystemRights rights) =>
            new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow);

        private static void Protect(Session session, string path, params FileSystemAccessRule[] rules)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("A folder path is missing.");
            Directory.CreateDirectory(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false); // break inheritance, drop inherited entries
            foreach (var rule in rules)
                security.AddAccessRule(rule);
            // SetNamedSecurityInfo propagates the inheritable entries to the existing files and sub-folders.
            Directory.SetAccessControl(path, security);
            session.Log("WinSmtpRelay HardenFolders: {0} -> {1}", path,
                string.Join(", ", rules.Select(r => r.IdentityReference.Value + ":" + r.FileSystemRights)));
        }

        // ============================================================================================
        // SYSTEM updater task (decision T1076). The definition comes from resources embedded in THIS assembly (the
        // task XML template + Update-WinSmtpRelay.ps1), never from a folder the service account can write. Deferred, as
        // SYSTEM, after HardenFolders; the temporary XML file is written into the SYSTEM-only updater-work folder.
        // CustomActionData: "WORK=<updater-work folder>".
        // ============================================================================================

        [CustomAction]
        public static ActionResult RegisterUpdateTask(Session session)
        {
            string xmlPath = null;
            try
            {
                var data = InstallerInput.ParseCustomActionData(session["CustomActionData"], "WORK");
                var work = Value(data, "WORK");
                Directory.CreateDirectory(work);

                var xml = UpdateTaskDefinition.Build(
                    UpdateTaskDefinition.ReadResource(UpdateTaskDefinition.TemplateResourceName),
                    UpdateTaskDefinition.ReadResource(UpdateTaskDefinition.ScriptResourceName));
                xmlPath = Path.Combine(work, "update-task-" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(xmlPath, xml, Encoding.Unicode);

                int exit = RunSchtasks(session, "/Create /TN " + UpdateTaskDefinition.TaskName + " /XML \"" + xmlPath + "\" /F");
                if (exit != 0)
                {
                    session.Log("WinSmtpRelay RegisterUpdateTask FAILED: schtasks exit code {0}.", exit);
                    return ActionResult.Failure;
                }
                session.Log("WinSmtpRelay RegisterUpdateTask: task {0} registered.", UpdateTaskDefinition.TaskName);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay RegisterUpdateTask FAILED: " + ex);
                return ActionResult.Failure;
            }
            finally
            {
                if (xmlPath != null)
                {
                    try { File.Delete(xmlPath); } catch { /* best effort */ }
                }
            }
        }

        [CustomAction]
        public static ActionResult UnregisterUpdateTask(Session session)
        {
            try
            {
                int exit = RunSchtasks(session, "/Delete /TN " + UpdateTaskDefinition.TaskName + " /F");
                session.Log("WinSmtpRelay UnregisterUpdateTask: schtasks exit code {0}.", exit);
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay UnregisterUpdateTask failed: " + ex);
            }
            return ActionResult.Success; // a task that is already gone is fine
        }

        private static int RunSchtasks(Session session, string arguments)
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
            var info = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                session.Log("WinSmtpRelay schtasks {0}: {1}", arguments, output.Trim());
                return process.ExitCode;
            }
        }

        // ============================================================================================
        // REMOVEDATA=1 on uninstall: delete the data folder (database = the operator's mail history, keys,
        // configuration). Default is to keep it. Never runs during a major upgrade (the old product's removal).
        // CustomActionData: "DATA=<data folder>".
        // ============================================================================================

        [CustomAction]
        public static ActionResult RemoveData(Session session)
        {
            try
            {
                var data = InstallerInput.ParseCustomActionData(session["CustomActionData"], "DATA");
                var folder = Value(data, "DATA");
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                    session.Log("WinSmtpRelay RemoveData: deleted {0}.", folder);
                }
                var parent = Path.GetDirectoryName(folder.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }
            catch (Exception ex)
            {
                session.Log("WinSmtpRelay RemoveData failed (the remaining files stay): " + ex);
            }
            return ActionResult.Success;
        }

        private static string Value(Dictionary<string, string> data, string key)
        {
            if (!data.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
                throw new ArgumentException("CustomActionData has no " + key + ".");
            return value;
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
    }
}
