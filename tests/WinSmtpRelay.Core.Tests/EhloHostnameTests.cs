using WinSmtpRelay.Core;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class EhloHostnameTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void Normalize_Fqdn_ReturnsTrimmed()
    {
        // DNS answers and copy-pasted zone entries often carry a trailing root dot; EHLO must not.
        Assert.AreEqual("relay.example.com", EhloHostname.Normalize(" relay.example.com. "));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Normalize_SingleLabel_ReturnsNull()
    {
        // A bare machine name is exactly what strict receivers reject ("neither a FQDN nor a IP literal").
        Assert.IsNull(EhloHostname.Normalize("winsmtprelay"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Normalize_NullOrWhitespace_ReturnsNull()
    {
        Assert.IsNull(EhloHostname.Normalize(null));
        Assert.IsNull(EhloHostname.Normalize("   "));
        Assert.IsNull(EhloHostname.Normalize("."));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Resolve_ConnectorOverride_WinsOverPublicHostname()
    {
        Assert.AreEqual("out2.example.com", EhloHostname.Resolve("out2.example.com", "relay.example.com"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Resolve_UnqualifiedConnectorOverride_FallsBackToPublicHostname()
    {
        Assert.AreEqual("relay.example.com", EhloHostname.Resolve("badname", "relay.example.com"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Resolve_NoCandidates_FallsBackToMachineFqdnOrNull()
    {
        // Machine-dependent: either the machine's FQDN (contains a dot) or null when the machine has
        // no DNS suffix — never a bare single-label name.
        var resolved = EhloHostname.Resolve(null, null);
        if (resolved is not null)
            Assert.IsTrue(resolved.Contains('.'), $"Expected an FQDN or null, got \"{resolved}\"");
    }
}
