using WinSmtpRelay.Core.Health;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class ApiKeyExpiryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private static ApiKeyExpiryState Evaluate(DateTimeOffset? expiresUtc) =>
        ApiKeyExpiryPolicy.Evaluate(expiresUtc, Now, warningDays: 30, errorDays: 7);

    [TestMethod]
    [TestCategory("Unit")]
    public void NoExpiryDate_IsNoExpiry()
    {
        Assert.AreEqual(ApiKeyExpiryState.NoExpiry, Evaluate(null));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BeyondWarningWindow_IsValid()
    {
        Assert.AreEqual(ApiKeyExpiryState.Valid, Evaluate(Now.AddDays(31)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void WithinWarningWindow_IsSoon()
    {
        Assert.AreEqual(ApiKeyExpiryState.Soon, Evaluate(Now.AddDays(30)));
        Assert.AreEqual(ApiKeyExpiryState.Soon, Evaluate(Now.AddDays(8)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void WithinErrorWindow_IsImminent()
    {
        Assert.AreEqual(ApiKeyExpiryState.Imminent, Evaluate(Now.AddDays(7)));
        Assert.AreEqual(ApiKeyExpiryState.Imminent, Evaluate(Now.AddMinutes(1)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AtOrPastExpiry_IsExpired()
    {
        // Mirrors ApiKeyService.ValidateAsync, which refuses a key once ExpiresUtc <= now.
        Assert.AreEqual(ApiKeyExpiryState.Expired, Evaluate(Now));
        Assert.AreEqual(ApiKeyExpiryState.Expired, Evaluate(Now.AddDays(-3)));
    }
}
