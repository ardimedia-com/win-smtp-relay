using WinSmtpRelay.Core.Authorization;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class ApiKeyScopesTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void EmptyScopes_AreReadOnly()
    {
        var scopes = ApiKeyScopes.Parse(null);

        foreach (var area in ApiKeyScopes.Areas)
        {
            Assert.IsTrue(ApiKeyScopes.AllowsRead(scopes, area), $"scope-less key should read {area}");
            Assert.IsFalse(ApiKeyScopes.AllowsWrite(scopes, area), $"scope-less key must not write {area}");
        }
        Assert.IsFalse(ApiKeyScopes.AllowsBody(scopes), "scope-less key must not read bodies");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PresentScopes_LimitReadsToListedAreas()
    {
        var scopes = ApiKeyScopes.Parse("diag:read config:write");

        Assert.IsTrue(ApiKeyScopes.AllowsRead(scopes, ApiKeyScopes.Diag));
        Assert.IsFalse(ApiKeyScopes.AllowsWrite(scopes, ApiKeyScopes.Diag));
        // write implies read within its area
        Assert.IsTrue(ApiKeyScopes.AllowsRead(scopes, ApiKeyScopes.Config));
        Assert.IsTrue(ApiKeyScopes.AllowsWrite(scopes, ApiKeyScopes.Config));
        // unlisted areas are inaccessible once any scope is present
        Assert.IsFalse(ApiKeyScopes.AllowsRead(scopes, ApiKeyScopes.Admin));
        Assert.IsFalse(ApiKeyScopes.AllowsWrite(scopes, ApiKeyScopes.Admin));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BodyScope_IsNeverImplied()
    {
        var everythingButBody = ApiKeyScopes.Parse(
            string.Join(' ', ApiKeyScopes.Areas.Select(ApiKeyScopes.Write)));
        Assert.IsFalse(ApiKeyScopes.AllowsBody(everythingButBody));

        Assert.IsTrue(ApiKeyScopes.AllowsBody(ApiKeyScopes.Parse("messages:body")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Normalize_SortsDeduplicatesAndLowercases()
    {
        Assert.AreEqual("config:write diag:read", ApiKeyScopes.Normalize(["diag:read", "CONFIG:WRITE", "diag:read"]));
        Assert.IsNull(ApiKeyScopes.Normalize([]), "empty set normalizes to null (the read-only default)");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsKnown_AcceptsAllAreaScopesAndBody_RejectsGarbage()
    {
        foreach (var area in ApiKeyScopes.Areas)
        {
            Assert.IsTrue(ApiKeyScopes.IsKnown(ApiKeyScopes.Read(area)));
            Assert.IsTrue(ApiKeyScopes.IsKnown(ApiKeyScopes.Write(area)));
        }
        Assert.IsTrue(ApiKeyScopes.IsKnown(ApiKeyScopes.MessagesBody));
        Assert.IsFalse(ApiKeyScopes.IsKnown("diag"));
        Assert.IsFalse(ApiKeyScopes.IsKnown("root:write"));
    }
}
