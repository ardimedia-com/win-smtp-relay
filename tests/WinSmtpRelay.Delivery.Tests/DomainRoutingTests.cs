using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Delivery;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Delivery.Tests;

[TestClass]
public class DomainRoutingTests
{
    private SmtpDeliveryService CreateService(List<DomainRoute>? routes = null)
    {
        var mxResolver = new StubMxResolver();
        var dkimSigner = new DkimSigningService(
            Options.Create(new DkimOptions()),
            NullLogger<DkimSigningService>.Instance);

        var cache = new StubRuntimeConfigCache { DomainRoutes = routes ?? [] };

        return new SmtpDeliveryService(
            mxResolver,
            Options.Create(new DeliveryOptions()),
            cache,
            dkimSigner,
            new StubDkimDomainService(),
            NullLogger<SmtpDeliveryService>.Instance);
    }

    private static DomainRoute CreateRoute(string pattern, string host, int port = 587)
    {
        var connector = new SendConnector { Name = host, SmartHost = host, SmartHostPort = port };
        return new DomainRoute { DomainPattern = pattern, SendConnector = connector };
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FindDomainRouteAsync_ExactMatch_ReturnsRoute()
    {
        var service = CreateService([CreateRoute("example.com", "relay.sendgrid.net")]);
        var route = await service.FindDomainRouteAsync("example.com");

        Assert.IsNotNull(route);
        Assert.AreEqual("relay.sendgrid.net", route.SendConnector!.SmartHost);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FindDomainRouteAsync_ExactMatch_CaseInsensitive()
    {
        var service = CreateService([CreateRoute("Example.COM", "relay.sendgrid.net")]);
        var route = await service.FindDomainRouteAsync("example.com");
        Assert.IsNotNull(route);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FindDomainRouteAsync_WildcardMatch_Subdomain()
    {
        var service = CreateService([CreateRoute("*.example.com", "smtp.brevo.com")]);
        var route = await service.FindDomainRouteAsync("sub.example.com");
        Assert.IsNotNull(route);
        Assert.AreEqual("smtp.brevo.com", route.SendConnector!.SmartHost);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FindDomainRouteAsync_WildcardMatch_BaseDomain()
    {
        var service = CreateService([CreateRoute("*.example.com", "smtp.brevo.com")]);
        // *.example.com should also match example.com itself
        var route = await service.FindDomainRouteAsync("example.com");
        Assert.IsNotNull(route);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FindDomainRouteAsync_NoMatch_ReturnsNull()
    {
        var service = CreateService([CreateRoute("example.com", "relay.sendgrid.net")]);
        var route = await service.FindDomainRouteAsync("other.com");
        Assert.IsNull(route);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FindDomainRouteAsync_EmptyRoutes_ReturnsNull()
    {
        var service = CreateService();
        var route = await service.FindDomainRouteAsync("example.com");
        Assert.IsNull(route);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FindDomainRouteAsync_FirstMatchWins()
    {
        var service = CreateService([
            CreateRoute("example.com", "first.host"),
            CreateRoute("example.com", "second.host")
        ]);
        var route = await service.FindDomainRouteAsync("example.com");
        Assert.IsNotNull(route);
        Assert.AreEqual("first.host", route.SendConnector!.SmartHost);
    }

    private class StubMxResolver : IMxResolver
    {
        public Task<IReadOnlyList<string>> ResolveMxAsync(string domain, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(["mx.example.com"]);
    }
}
