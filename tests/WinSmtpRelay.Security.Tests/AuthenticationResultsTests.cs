using WinSmtpRelay.Security.Models;

namespace WinSmtpRelay.Security.Tests;

[TestClass]
public class AuthenticationResultsTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void ToHeaderValue_SpfPass_ContainsSpfPass()
    {
        var results = new AuthenticationResults(
            new SpfCheckResult(SpfVerdict.Pass, "ip4 matched"),
            new DmarcCheckResult(DmarcVerdict.None, DmarcPolicy.None, ""));

        var header = results.ToHeaderValue("winsmtprelay");
        Assert.IsTrue(header.Contains("spf=pass"));
        Assert.IsTrue(header.Contains("winsmtprelay"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ToHeaderValue_SpfAndDmarc_ContainsBoth()
    {
        var results = new AuthenticationResults(
            new SpfCheckResult(SpfVerdict.Pass, "matched"),
            new DmarcCheckResult(DmarcVerdict.Pass, DmarcPolicy.Reject, "aligned"));

        var header = results.ToHeaderValue("winsmtprelay");
        Assert.IsTrue(header.Contains("spf=pass"));
        Assert.IsTrue(header.Contains("dmarc=pass"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ShouldReject_SpfFail_ReturnsTrue()
    {
        var results = new AuthenticationResults(
            new SpfCheckResult(SpfVerdict.Fail, ""),
            new DmarcCheckResult(DmarcVerdict.None, DmarcPolicy.None, ""));

        Assert.IsTrue(results.ShouldReject);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ShouldReject_SpfPass_ReturnsFalse()
    {
        var results = new AuthenticationResults(
            new SpfCheckResult(SpfVerdict.Pass, ""),
            new DmarcCheckResult(DmarcVerdict.None, DmarcPolicy.None, ""));

        Assert.IsFalse(results.ShouldReject);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ShouldQuarantine_SpfSoftFail_ReturnsTrue()
    {
        var results = new AuthenticationResults(
            new SpfCheckResult(SpfVerdict.SoftFail, ""),
            new DmarcCheckResult(DmarcVerdict.None, DmarcPolicy.None, ""));

        Assert.IsTrue(results.ShouldQuarantine);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ShouldReject_SpfFailButDmarcPassesViaDkim_ReturnsFalse()
    {
        // The forwarded-mail case: a DMARC record exists and DKIM aligns, so SPF alone must not reject.
        var results = new AuthenticationResults(
            new SpfCheckResult(SpfVerdict.Fail, ""),
            new DmarcCheckResult(DmarcVerdict.Pass, DmarcPolicy.Reject, "DKIM aligned"));

        Assert.IsFalse(results.ShouldReject);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(DmarcPolicy.None, 100, false)]
    [DataRow(DmarcPolicy.Quarantine, 100, false)]
    [DataRow(DmarcPolicy.Reject, 100, true)]
    [DataRow(DmarcPolicy.Reject, 50, false)]
    public void ShouldReject_DmarcFail_FollowsThePolicy(DmarcPolicy policy, int pct, bool expected)
    {
        var results = new AuthenticationResults(
            new SpfCheckResult(SpfVerdict.Fail, ""),
            new DmarcCheckResult(DmarcVerdict.Fail, policy, "not aligned", pct));

        Assert.AreEqual(expected, results.ShouldReject);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ShouldDefer_SpfFailAndDmarcTempError_ReturnsTrue()
    {
        // "Could not look up" is not "no record": neither reject on SPF alone nor accept.
        var results = new AuthenticationResults(
            new SpfCheckResult(SpfVerdict.Fail, ""),
            new DmarcCheckResult(DmarcVerdict.TempError, DmarcPolicy.None, "DNS error"));

        Assert.IsFalse(results.ShouldReject);
        Assert.IsTrue(results.ShouldDefer);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ShouldQuarantine_DmarcPassUnderQuarantinePolicy_ReturnsFalse()
    {
        var results = new AuthenticationResults(
            new SpfCheckResult(SpfVerdict.Pass, ""),
            new DmarcCheckResult(DmarcVerdict.Pass, DmarcPolicy.Quarantine, "aligned"));

        Assert.IsFalse(results.ShouldQuarantine);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ToHeaderValue_BothNone_ReturnsJustServerId()
    {
        var results = new AuthenticationResults(
            new SpfCheckResult(SpfVerdict.None, ""),
            new DmarcCheckResult(DmarcVerdict.None, DmarcPolicy.None, ""));

        var header = results.ToHeaderValue("winsmtprelay");
        Assert.AreEqual("winsmtprelay", header);
    }
}
