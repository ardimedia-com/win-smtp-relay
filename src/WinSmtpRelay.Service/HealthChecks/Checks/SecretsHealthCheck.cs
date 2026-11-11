using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Service.HealthChecks.Checks;

/// <summary>
/// Stored secrets that cannot be decrypted on this machine (M12): the smart-host passwords, the DKIM private keys
/// and the imported admin certificate are DPAPI-protected with the machine key, so a database restored onto another
/// machine carries ciphertext the relay cannot use. Delivery fails closed on them (temporary failure, nothing is
/// sent with a wrong password or an unusable key); this check names each one so the operator re-enters it.
/// </summary>
public sealed class SecretsHealthCheck(RelayDbContext db) : HealthCheckBase
{
    public override string Name => "Secrets";
    protected override string Category => HealthCategories.Security;

    public override async Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct)
    {
        var f = new List<HealthFinding>();
        const string hint = "The database was probably restored from another machine (DPAPI keys are per machine). Enter the secret again in the admin UI.";

        var connectors = await db.SendConnectors.IgnoreQueryFilters().AsNoTracking()
            .Select(c => new { c.Name, c.SmartHost, c.EncryptedPassword }).ToListAsync(ct);
        foreach (var c in connectors.Where(c => SecretProtector.IsUnavailable(c.EncryptedPassword)))
            f.Add(Err("secret-undecryptable", $"Smart-host password of send connector {c.Name} cannot be decrypted",
                $"Mail through {c.SmartHost} waits (temporary failure) until the password is entered again.", c.Name, hint));

        var dkim = await db.DkimDomains.IgnoreQueryFilters().AsNoTracking()
            .Select(d => new { d.Domain, d.IsEnabled, d.PrivateKeyPem }).ToListAsync(ct);
        foreach (var d in dkim.Where(d => SecretProtector.IsUnavailable(d.PrivateKeyPem)))
            f.Add(Err("secret-undecryptable", $"DKIM private key for {d.Domain} cannot be decrypted",
                d.IsEnabled
                    ? $"Mail from {d.Domain} waits (temporary failure) instead of leaving unsigned."
                    : "The key is disabled, so no mail is held — but it cannot be enabled again as it is.",
                d.Domain, "Generate or import the DKIM key again (and publish the new DNS record), or restore the database on its original machine."));

        var cert = await db.AdminCertificateSettings.AsNoTracking().Select(s => s.PfxBase64).FirstOrDefaultAsync(ct);
        if (SecretProtector.IsUnavailable(cert))
            f.Add(Err("secret-undecryptable", "The imported admin-UI certificate cannot be decrypted",
                "The admin UI falls back to its default certificate.", "admin certificate", "Import the certificate again."));

        if (f.Count == 0)
            f.Add(Ok("secrets-readable", "Stored secrets can be decrypted",
                "Smart-host passwords, DKIM keys and the imported certificate are readable on this machine."));
        return f;
    }
}
