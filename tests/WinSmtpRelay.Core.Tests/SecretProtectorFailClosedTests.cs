using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

/// <summary>M12: a protected value that cannot be decrypted is never handed back as if it were the secret.</summary>
[TestClass]
public class SecretProtectorFailClosedTests
{
    // Valid marker + base64, but not a DPAPI blob of this machine (as after a restore onto another host).
    private const string Foreign = "dpapi:AAECAwQFBgcICQoLDA0ODw==";

    [TestMethod]
    [TestCategory("Unit")]
    public void Unprotect_Undecryptable_Throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI only applies on Windows.");
            return;
        }

        Assert.ThrowsExactly<SecretUnavailableException>(() => SecretProtector.Unprotect(Foreign));
        Assert.ThrowsExactly<SecretUnavailableException>(() => SecretProtector.Unprotect("dpapi:not-base64!"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ConverterRead_KeepsTheCiphertext_AndConsumersRefuseIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI only applies on Windows.");
            return;
        }

        var read = SecretProtector.UnprotectOrKeep(Foreign);
        Assert.AreEqual(Foreign, read, "the row stays readable and editable");
        Assert.IsTrue(SecretProtector.IsUnavailable(read));
        Assert.ThrowsExactly<SecretUnavailableException>(() => SecretProtector.RequireUsable(read, "smart-host password"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Protect_NeverWrapsCiphertextTwice()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI only applies on Windows.");
            return;
        }

        Assert.AreEqual(Foreign, SecretProtector.Protect(Foreign), "an undecryptable value is saved back unchanged");
        var once = SecretProtector.Protect("secret")!;
        Assert.AreEqual(once, SecretProtector.Protect(once));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void UsableValues_PassThrough()
    {
        Assert.AreEqual("plain", SecretProtector.RequireUsable("plain", "x"));
        Assert.IsNull(SecretProtector.RequireUsable(null, "x"));
        Assert.IsFalse(SecretProtector.IsUnavailable("plain"));
        Assert.IsFalse(SecretProtector.IsUnavailable(null));
    }
}
