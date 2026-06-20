using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Security.Tests;

[TestClass]
public class OutboundAlignmentTests
{
    private static DnsRecordResult Rec(DnsRecordStatus status) =>
        new("X", "name", "expected", "live", status, "");

    // ----- DmarcAlignment.Evaluate -----

    [TestMethod]
    [TestCategory("Unit")]
    public void Evaluate_DkimOk_IsDkimAligned()
    {
        var a = DmarcAlignment.Evaluate(Rec(DnsRecordStatus.Missing), Rec(DnsRecordStatus.Ok), Rec(DnsRecordStatus.Ok));
        Assert.AreEqual(DmarcAlignmentVerdict.DkimAligned, a.Verdict);
        Assert.IsTrue(a.DkimAligned);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Evaluate_OnlySpfOk_IsSpfConditional()
    {
        var a = DmarcAlignment.Evaluate(Rec(DnsRecordStatus.Ok), Rec(DnsRecordStatus.Missing), Rec(DnsRecordStatus.Ok));
        Assert.AreEqual(DmarcAlignmentVerdict.SpfConditional, a.Verdict);
        Assert.IsTrue(a.SpfAuthorized);
        Assert.IsFalse(a.DkimAligned);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Evaluate_NeitherOk_Fails()
    {
        var a = DmarcAlignment.Evaluate(Rec(DnsRecordStatus.Missing), Rec(DnsRecordStatus.NotConfigured), Rec(DnsRecordStatus.Missing));
        Assert.AreEqual(DmarcAlignmentVerdict.Fail, a.Verdict);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Evaluate_DmarcNotPublished_NotedInSummary()
    {
        var a = DmarcAlignment.Evaluate(Rec(DnsRecordStatus.Ok), Rec(DnsRecordStatus.Ok), Rec(DnsRecordStatus.Missing));
        Assert.IsFalse(a.DmarcPublished);
        StringAssert.Contains(a.Summary, "No DMARC record is published");
    }

    // ----- EnvelopeAlignment (uses the real Public Suffix List) -----

    private static readonly PublicSuffixService Psl = new();

    [TestMethod]
    [TestCategory("Unit")]
    public void IsAligned_ExactMatch_True() =>
        Assert.IsTrue(EnvelopeAlignment.IsAligned("weelinq.com", "weelinq.com", Psl));

    [TestMethod]
    [TestCategory("Unit")]
    public void IsAligned_Subdomain_RelaxedTrue_StrictFalse()
    {
        Assert.IsTrue(EnvelopeAlignment.IsAligned("bounces.weelinq.com", "weelinq.com", Psl));
        Assert.IsFalse(EnvelopeAlignment.IsAligned("bounces.weelinq.com", "weelinq.com", Psl, strict: true));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsAligned_DifferentOrg_False() =>
        Assert.IsFalse(EnvelopeAlignment.IsAligned("vendor.com", "weelinq.com", Psl));

    [TestMethod]
    [TestCategory("Unit")]
    public void IsAligned_MultiLabelSuffix_UsesRegistrableDomain()
    {
        // co.uk is a public suffix → registrable domain is foo.co.uk, not co.uk.
        Assert.IsTrue(EnvelopeAlignment.IsAligned("mail.foo.co.uk", "foo.co.uk", Psl));
        Assert.IsFalse(EnvelopeAlignment.IsAligned("foo.co.uk", "bar.co.uk", Psl));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsAligned_MissingDomain_False()
    {
        Assert.IsFalse(EnvelopeAlignment.IsAligned(null, "weelinq.com", Psl));
        Assert.IsFalse(EnvelopeAlignment.IsAligned("weelinq.com", "", Psl));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DomainOf_ExtractsDomain()
    {
        Assert.AreEqual("a.com", EnvelopeAlignment.DomainOf("user@a.com"));
        Assert.AreEqual("a.com", EnvelopeAlignment.DomainOf("user@a.com>"));
        Assert.IsNull(EnvelopeAlignment.DomainOf(""));
        Assert.IsNull(EnvelopeAlignment.DomainOf(null));
    }
}
