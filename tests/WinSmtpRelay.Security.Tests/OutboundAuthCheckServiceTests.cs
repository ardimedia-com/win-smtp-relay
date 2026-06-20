using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Security.Tests;

[TestClass]
public class OutboundAuthCheckServiceTests
{
    private static DnsRecordResult Rec(DnsRecordStatus status) => new("X", "name", "expected", "live", status, "");

    private static OutboundAuthCheckService Build(DkimDomain? key, DomainDnsSetup dns)
    {
        var signer = new DkimSigningService(Options.Create(new DkimOptions { Enabled = false }), NullLogger<DkimSigningService>.Instance);
        return new OutboundAuthCheckService(
            signer, new StubDkim(key), new StubDnsSetup(dns), new PublicSuffixService(),
            NullLogger<OutboundAuthCheckService>.Instance);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CheckAsync_WithTenantKey_SignsAndVerifiesAligned()
    {
        using var rsa = RSA.Create(2048);
        var key = new DkimDomain { Domain = "example.com", Selector = "test", PrivateKeyPem = rsa.ExportRSAPrivateKeyPem(), TenantId = 1 };
        var dns = new DomainDnsSetup("example.com", Rec(DnsRecordStatus.Ok), Rec(DnsRecordStatus.Ok), Rec(DnsRecordStatus.Ok));

        var result = await Build(key, dns).CheckAsync(1, "noreply@example.com");

        Assert.IsTrue(result.DkimSigned, "a key is configured, so the message should be signed");
        Assert.IsTrue(result.DkimSignatureValid, "the produced signature must verify against the relay's own key");
        Assert.IsTrue(result.DkimAligned);
        Assert.AreEqual("example.com", result.DkimSigningDomain);
        Assert.IsTrue(result.WillPassDmarc);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CheckAsync_NoKey_ReportsUnsigned()
    {
        var dns = new DomainDnsSetup("example.com", Rec(DnsRecordStatus.Ok), Rec(DnsRecordStatus.Missing), Rec(DnsRecordStatus.Ok));

        var result = await Build(null, dns).CheckAsync(1, "noreply@example.com");

        Assert.IsFalse(result.DkimSigned);
        Assert.IsFalse(result.WillPassDmarc);
        Assert.AreEqual(DmarcAlignmentVerdict.SpfConditional, result.Alignment.Verdict);
    }

    private sealed class StubDkim(DkimDomain? key) : IDkimDomainService
    {
        public Task<DkimDomain?> GetForSigningAsync(int tenantId, string domain, CancellationToken ct = default) => Task.FromResult(key);
        public Task<IReadOnlyList<DkimDomain>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DkimDomain>>(key is null ? [] : [key]);
        public Task<DkimDomain?> GetByDomainAsync(string domain, CancellationToken ct = default) => Task.FromResult(key);
        public Task<DkimDomain> CreateAsync(DkimDomain dkim, CancellationToken ct = default) => Task.FromResult(dkim);
        public Task UpdateAsync(DkimDomain dkim, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubDnsSetup(DomainDnsSetup setup) : IDnsSetupService
    {
        public Task<DomainDnsSetup> CheckDomainAsync(string domain, CancellationToken ct = default) => Task.FromResult(setup);
        public Task<IReadOnlyList<DomainDnsSetup>> CheckAllSenderDomainsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DomainDnsSetup>>([setup]);
        public string BuildRecommendedSpf() => "";
        public string BuildRecommendedDmarc(string domain) => "";
        public Task<DnsRecordResult> CheckMxAsync(string domain, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DnsRecordResult> CheckReverseDnsAsync(string ipAddress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DnsRecordResult> CheckHostnameAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DnsRecordResult> CheckBlocklistsAsync(string ipAddress, CancellationToken ct = default) => throw new NotImplementedException();
        public string BuildOwnershipRecord(string token) => "";
        public Task<bool> CheckOwnershipAsync(string domain, string token, CancellationToken ct = default) => Task.FromResult(false);
    }
}
