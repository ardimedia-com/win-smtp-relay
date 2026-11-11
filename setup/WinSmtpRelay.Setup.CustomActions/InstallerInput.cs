using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WinSmtpRelay.Setup.CustomActions
{
    /// <summary>
    /// Validation of the values that flow from installer properties into the elevated custom actions. Pure functions,
    /// so they can be tested without Windows Installer.
    /// </summary>
    public static class InstallerInput
    {
        /// <summary>
        /// A TCP port: 1 to 65535, ASCII digits only (no sign, no whitespace, no separators). Anything else — in
        /// particular a value carrying "|" or "=" to smuggle extra CustomActionData keys — is rejected.
        /// </summary>
        public static bool TryParsePort(string value, out int port)
        {
            port = 0;
            if (string.IsNullOrEmpty(value) || value.Length > 5 || value.Any(c => c < '0' || c > '9'))
                return false;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port))
                return false;
            return port >= 1 && port <= 65535;
        }

        /// <summary>
        /// Parses CustomActionData of the form "KEY=value|KEY=value". Throws when a key is not in
        /// <paramref name="allowedKeys"/>, appears twice, or a pair has no "=": a property value that contains "|" can
        /// therefore never inject or override a key.
        /// </summary>
        public static Dictionary<string, string> ParseCustomActionData(string raw, params string[] allowedKeys)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw))
                return result;

            var allowed = new HashSet<string>(allowedKeys ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (var pair in raw.Split('|'))
            {
                int idx = pair.IndexOf('=');
                if (idx <= 0)
                    throw new FormatException("CustomActionData contains a malformed entry.");
                var key = pair.Substring(0, idx);
                if (!allowed.Contains(key))
                    throw new FormatException("CustomActionData contains the unexpected key '" + key + "'.");
                if (result.ContainsKey(key))
                    throw new FormatException("CustomActionData contains the key '" + key + "' twice.");
                result[key] = pair.Substring(idx + 1);
            }
            return result;
        }
    }
}
