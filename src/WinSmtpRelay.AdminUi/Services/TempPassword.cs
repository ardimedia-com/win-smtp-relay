using System.Security.Cryptography;

namespace WinSmtpRelay.AdminUi.Services;

/// <summary>Generates a strong one-time password for an admin account created/reset by another admin
/// (the account is flagged MustChangePassword). Guarantees one of each character class.</summary>
public static class TempPassword
{
    public static string Generate()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digits = "23456789", symbols = "!@#$%^&*-_=+";
        const string all = upper + lower + digits + symbols;
        Span<char> b = stackalloc char[20];
        b[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        b[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        b[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        b[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var i = 4; i < b.Length; i++)
            b[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        for (var i = b.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (b[i], b[j]) = (b[j], b[i]);
        }
        return new string(b);
    }
}
