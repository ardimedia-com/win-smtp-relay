using WinSmtpRelay.AdminUi.Services;

namespace WinSmtpRelay.AdminUi.Tests;

[TestClass]
public sealed class InputValidationTests
{
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("Example.COM", "example.com")]
    [DataRow("  mail.example.com.  ", "mail.example.com")]
    [DataRow("bücher.ch", "xn--bcher-kva.ch")]
    [DataRow("xn--bcher-kva.ch", "xn--bcher-kva.ch")]
    public void TryNormalizeDomain_ValidInput_ReturnsLowerCasePunycode(string input, string expected)
    {
        var ok = InputValidation.TryNormalizeDomain(input, out var domain, out var error);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(expected, domain);
        Assert.IsNull(error);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("")]
    [DataRow("localhost")]
    [DataRow("exa mple.com")]
    [DataRow("example..com")]
    [DataRow("user@example.com")]
    [DataRow("10.0.0.1")]
    [DataRow("-bad-.com")]
    public void TryNormalizeDomain_InvalidInput_ReturnsMessage(string input)
    {
        var ok = InputValidation.TryNormalizeDomain(input, out _, out var error);

        Assert.IsFalse(ok);
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("10.0.0.0/8", "10.0.0.0/8")]
    [DataRow(" 192.168.1.10 ", "192.168.1.10")]
    [DataRow("2001:DB8::/48", "2001:db8::/48")]
    [DataRow("0.0.0.0/0", "0.0.0.0/0")]
    public void TryNormalizeNetwork_ValidInput_ReturnsCanonicalForm(string input, string expected)
    {
        var ok = InputValidation.TryNormalizeNetwork(input, out var network, out var error);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(expected, network);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("")]
    [DataRow("10.0.0.0/33")]
    [DataRow("10.0.0.0/-1")]
    [DataRow("10.0.0/8")]
    [DataRow("10/8")]
    [DataRow("10.1")]
    [DataRow("2001:db8::/129")]
    [DataRow("example.com")]
    [DataRow("10.0.0.0/abc")]
    public void TryNormalizeNetwork_InvalidInput_ReturnsMessage(string input)
    {
        var ok = InputValidation.TryNormalizeNetwork(input, out _, out var error);

        Assert.IsFalse(ok);
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TryParseInt_WithinRange_ReturnsValue()
    {
        Assert.IsTrue(InputValidation.TryParseInt(" 587 ", "Port", 1, 65535, out var value, out _));
        Assert.AreEqual(587, value);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("abc")]
    [DataRow("")]
    [DataRow("-1")]
    [DataRow("70000")]
    [DataRow("1.5")]
    public void TryParseInt_InvalidOrOutOfRange_NamesTheField(string input)
    {
        var ok = InputValidation.TryParseInt(input, "Port", 1, 65535, out _, out var error);

        Assert.IsFalse(ok);
        StringAssert.StartsWith(error, "Port:");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TryParseMegabytesToBytes_2047Mb_DoesNotOverflow()
    {
        Assert.IsTrue(InputValidation.TryParseMegabytesToBytes("2047", "Max size", 1, out var bytes, out _));
        Assert.AreEqual(2047L * 1024 * 1024, bytes);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TryParseMegabytesToBytes_2048Mb_IsRejectedInsteadOfOverflowing()
    {
        var ok = InputValidation.TryParseMegabytesToBytes("2048", "Max size", 1, out _, out var error);

        Assert.IsFalse(ok);
        Assert.IsNotNull(error);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TryParseRetryIntervals_ValidList_ReturnsCanonicalForm()
    {
        Assert.IsTrue(InputValidation.TryParseRetryIntervals("1, 5;30  120", out var normalized, out _));
        Assert.AreEqual("1,5,30,120", normalized);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("")]
    [DataRow("1,x,5")]
    [DataRow("0,5")]
    [DataRow("-5")]
    [DataRow("20000")]
    public void TryParseRetryIntervals_Invalid_ReturnsMessage(string input)
    {
        var ok = InputValidation.TryParseRetryIntervals(input, out _, out var error);

        Assert.IsFalse(ok);
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("user@example.com", true)]
    [DataRow(" user@example.com ", true)]
    [DataRow("user@localhost", false)]
    [DataRow("user example@x.com", false)]
    [DataRow("a@b@c.com", false)]
    [DataRow("@example.com", false)]
    [DataRow("user@", false)]
    public void IsPlausibleEmail(string input, bool expected)
    {
        Assert.AreEqual(expected, InputValidation.IsPlausibleEmail(input));
    }
}
