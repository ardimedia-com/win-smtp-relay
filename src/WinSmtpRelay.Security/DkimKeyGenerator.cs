using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace WinSmtpRelay.Security;

public static class DkimKeyGenerator
{
    public static (string PrivateKeyPem, string PublicKeyPem, string DnsTxtValue) GenerateKeyPair(
        string domain, string selector, int keySize = 2048)
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), keySize));
        var keyPair = generator.GenerateKeyPair();

        var privatePem = ToPem(keyPair.Private, "RSA PRIVATE KEY");
        var publicPem = ToPem(keyPair.Public, "PUBLIC KEY");

        // Extract base64 public key for DNS TXT record (strip PEM headers)
        var publicKeyBase64 = publicPem
            .Replace("-----BEGIN PUBLIC KEY-----", "")
            .Replace("-----END PUBLIC KEY-----", "")
            .Replace("\r", "")
            .Replace("\n", "");

        var dnsTxt = $"v=DKIM1; k=rsa; p={publicKeyBase64}";

        return (privatePem, publicPem, dnsTxt);
    }

    /// <summary>
    /// The DNS TXT value (<c>v=DKIM1; k=rsa; p=…</c>) for a PEM private key (PKCS#1 or PKCS#8). Throws when the PEM
    /// is not an RSA private key.
    /// </summary>
    public static string DeriveDnsTxtValue(string privateKeyPem)
    {
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    private static string ToPem(AsymmetricKeyParameter key, string type)
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        var pemWriter = new PemWriter(writer);
        pemWriter.WriteObject(key);
        pemWriter.Writer.Flush();
        return sb.ToString();
    }
}
