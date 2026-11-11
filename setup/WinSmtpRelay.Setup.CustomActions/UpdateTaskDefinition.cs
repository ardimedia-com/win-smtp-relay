using System;
using System.IO;
using System.Linq;
using System.Text;

namespace WinSmtpRelay.Setup.CustomActions
{
    /// <summary>
    /// Builds the definition of the SYSTEM updater task from the two embedded files (decision T1076): the task XML
    /// template and the updater script, which becomes the task's base64 <c>-EncodedCommand</c>. Pure string work, so
    /// the result can be tested without Windows Installer.
    /// </summary>
    public static class UpdateTaskDefinition
    {
        /// <summary>Name of the scheduled task.</summary>
        public const string TaskName = "WinSmtpRelayUpdate";

        /// <summary>Placeholder in the template that receives the encoded script.</summary>
        public const string Placeholder = "{ENCODED_COMMAND}";

        /// <summary>
        /// A task's action arguments and the resulting command line must stay well below the 32,767-character limit
        /// of CreateProcess; the installer refuses to register a larger definition.
        /// </summary>
        public const int MaxEncodedCommandLength = 30000;

        public const string TemplateResourceName = "WinSmtpRelay.Setup.CustomActions.update-task.xml";
        public const string ScriptResourceName = "WinSmtpRelay.Setup.CustomActions.Update-WinSmtpRelay.ps1";

        /// <summary>
        /// The script with full-line comments and blank lines removed (they only cost command-line length). Lines inside
        /// the script's here-strings never start with '#', so stripping cannot change code.
        /// </summary>
        public static string CompactScript(string script)
        {
            var lines = script.Replace("\r\n", "\n").Split('\n')
                .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#", StringComparison.Ordinal));
            return string.Join("\r\n", lines) + "\r\n";
        }

        /// <summary>The script as PowerShell's -EncodedCommand expects it: base64 of the UTF-16LE text.</summary>
        public static string EncodeCommand(string script) =>
            Convert.ToBase64String(Encoding.Unicode.GetBytes(CompactScript(script)));

        /// <summary>The final task XML: template without its leading comment, placeholder replaced.</summary>
        public static string Build(string template, string script)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (script == null) throw new ArgumentNullException(nameof(script));

            int start = template.IndexOf("<Task", StringComparison.Ordinal);
            if (start < 0 || template.IndexOf(Placeholder, StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("The updater task template is malformed (no <Task> element or no placeholder).");

            var encoded = EncodeCommand(script);
            if (encoded.Length > MaxEncodedCommandLength)
                throw new InvalidOperationException(
                    "The encoded updater script is " + encoded.Length + " characters long; the limit is " + MaxEncodedCommandLength + ".");

            return template.Substring(start).Replace(Placeholder, encoded);
        }

        /// <summary>Reads an embedded text resource of this assembly.</summary>
        public static string ReadResource(string name)
        {
            using (var stream = typeof(UpdateTaskDefinition).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null)
                    throw new InvalidOperationException("Embedded resource " + name + " is missing.");
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }
    }
}
