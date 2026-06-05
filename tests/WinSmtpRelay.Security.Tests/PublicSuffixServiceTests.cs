namespace WinSmtpRelay.Security.Tests;

[TestClass]
public class PublicSuffixServiceTests
{
    // A small hand-written list exercising the four rule kinds (normal, multi-level, wildcard, exception)
    // plus a PRIVATE section that must be ignored.
    private const string List = """
        // ===BEGIN ICANN DOMAINS===
        com
        org
        uk
        co.uk
        // a wildcard TLD and its exception
        ck
        *.ck
        !www.ck
        jp
        // ===BEGIN PRIVATE DOMAINS===
        blogspot.com
        """;

    private static readonly PublicSuffixService Psl = new(List);

    [TestMethod]
    [TestCategory("Unit")]
    public void GetRegistrableDomain_StripsSubdomainsToTheOrganizationalDomain()
    {
        Assert.AreEqual("ardimedia.com", Psl.GetRegistrableDomain("smtp2.ardimedia.com"));
        Assert.AreEqual("ardimedia.com", Psl.GetRegistrableDomain("ardimedia.com"));
        Assert.AreEqual("example.org", Psl.GetRegistrableDomain("a.b.c.example.org"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void GetRegistrableDomain_HandlesMultiLevelPublicSuffix()
    {
        // co.uk is a public suffix, so the registrable domain keeps three labels.
        Assert.AreEqual("b.co.uk", Psl.GetRegistrableDomain("mail.a.b.co.uk"));
        Assert.AreEqual("acme.co.uk", Psl.GetRegistrableDomain("acme.co.uk"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void GetRegistrableDomain_HandlesWildcardAndExceptionRules()
    {
        // *.ck makes any "<x>.ck" a public suffix, so the registrable domain has three labels...
        Assert.AreEqual("foo.bar.ck", Psl.GetRegistrableDomain("a.foo.bar.ck"));
        // ...except !www.ck, which makes "www.ck" itself registrable (two labels).
        Assert.AreEqual("www.ck", Psl.GetRegistrableDomain("site.www.ck"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void GetRegistrableDomain_IgnoresPrivateSection_TreatingItAsIcannOnly()
    {
        // blogspot.com is in the PRIVATE section, so it is NOT a public suffix here: the ICANN
        // registrable domain is blogspot.com (suffix "com" + one label).
        Assert.AreEqual("blogspot.com", Psl.GetRegistrableDomain("myblog.blogspot.com"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void GetRegistrableDomain_ReturnsNull_ForSuffixOnly_IpLiteral_OrTooFewLabels()
    {
        Assert.IsNull(Psl.GetRegistrableDomain("co.uk"), "host is itself a public suffix");
        Assert.IsNull(Psl.GetRegistrableDomain("com"), "single-label public suffix");
        Assert.IsNull(Psl.GetRegistrableDomain("178.197.238.240"), "IPv4 literal");
        Assert.IsNull(Psl.GetRegistrableDomain("2001:db8::1"), "IPv6 literal");
        Assert.IsNull(Psl.GetRegistrableDomain(""));
        Assert.IsNull(Psl.GetRegistrableDomain(null));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void GetRegistrableDomain_NormalizesCaseAndTrailingDot()
    {
        Assert.AreEqual("ardimedia.com", Psl.GetRegistrableDomain("SMTP2.Ardimedia.COM."));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EmbeddedList_LoadsAndResolvesRealWorldDomains()
    {
        // The real embedded snapshot must parse and handle common cases.
        var psl = new PublicSuffixService();
        Assert.AreEqual("ardimedia.com", psl.GetRegistrableDomain("smtp2.ardimedia.com"));
        Assert.AreEqual("bbc.co.uk", psl.GetRegistrableDomain("www.bbc.co.uk"));
        Assert.AreEqual("example.gov.au", psl.GetRegistrableDomain("mail.example.gov.au"));
    }
}
