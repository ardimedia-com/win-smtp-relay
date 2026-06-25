# WIN-SMTP-RELAY

> **Work in progress** — This project is under active development and not yet ready for production use.

Open-source SMTP relay server for Windows. Built with .NET 10, designed as a modern replacement for IIS SMTP.

**Relay only** — no mailboxes, no IMAP, no POP3. Accepts mail from internal apps/devices and forwards it via MX lookup or smart host.

## Documentation

Full component API reference and guides: **https://ardimedia-com.github.io/win-smtp-relay/** (generated from
the source with DocFX). See also [Using the components](#using-the-components-embedding-the-engine-in-your-own-host) below.

## Features

- Multiple receive connectors (different port/IP/TLS/auth per connector)
- Send connectors with per-domain routing (MX or smart host)
- Store-and-forward message queue (SQLite)
- STARTTLS (port 587) and implicit TLS (port 465)
- SMTP AUTH with per-user SendAs control and rate limits
- DKIM signing (per tenant), inbound SPF/DKIM/DMARC verification (DMARC passes via SPF *or* DKIM alignment)
- Setup page (`/setup`): live per-organization readiness checklist (can-send + deliverability) with the DNS records to publish, a test-message sender, and a local authentication self-test ("Verify auth, no send") that builds, signs and verifies a message against the relay's own key to confirm it will pass DMARC
- Health page (`/health`): live deliverability checks per sender domain and sending IP — a synthesised **DMARC alignment outcome** per domain (passes via DKIM / conditional via SPF / will fail), plus SPF, DKIM, DMARC, MX, reverse DNS (PTR/FCrDNS), public-hostname A/AAAA, SPF coverage and 10-lookup-limit warnings, and DNSBL (Spamhaus ZEN / SpamCop), with copy-ready records
- Optional envelope-from (Return-Path) realignment on delivery (`Delivery:AlignReturnPath`) so SPF aligns for DMARC; off by default, with a warning logged when an unaligned envelope-from is detected
- Domain ownership verification (DNS TXT) for accepted sender and recipient domains, with optional enforcement on the SMTP path
- IP-based relay restrictions, with optional strict IP-to-tenant binding for unauthenticated submission (blocks cross-tenant sender-domain spoofing)
- Pickup folder for .eml files
- Blazor admin UI (HTTPS, loopback by default), sign-in required
- Authenticated admin access: cookie login + API keys, **membership-based authorization** (host admins + per-tenant admin/viewer roles, consent-based — a host admin gets access inside a tenant only by an explicit grant or an audited break-glass), **two-factor authentication (TOTP authenticator + recovery codes)**, an admin/security audit trail; optional passwordless sign-in link and email password-reset (both disableable per deployment for high-security installs)
- Multi-tenant: isolated per-tenant configuration and data, **a single admin can be delegated to several tenants**, a tenant switcher, per-tenant egress (source) IP, and optional self-service tenant signup
- REST API for management and monitoring
- Windows Service with Event Log integration
- MSI installer (WiX v5)

## Architecture

```
Internal App/Device
    |  SMTP (port 25/587/465)
    v
[WinSmtpRelay SMTP Listener]
    |
    v
[Message Queue (SQLite)]
    |
    v
[Delivery Engine (MailKit)]
    |
    v
External Mail Servers
```

## Technology Stack

| Component | Library |
|-----------|---------|
| SMTP Listener | SmtpServer (cosullivan/SmtpServer) |
| Outbound Delivery | MailKit + MimeKit |
| Queue Storage | SQLite + EF Core |
| DNS Resolver | DnsClient.NET |
| DKIM Signing | MimeKit DkimSigner |
| SPF/DMARC | Nager.EmailAuthentication |
| Admin UI | Blazor Server + Blazor Blueprint UI |
| Windows Service | Microsoft.Extensions.Hosting.WindowsServices |
| Installer | WiX v5 (MSI) |

## Solution Structure

```
src/
  WinSmtpRelay.Core          — Domain models, interfaces, configuration
  WinSmtpRelay.SmtpListener  — Inbound SMTP (wraps SmtpServer NuGet)
  WinSmtpRelay.Delivery      — Outbound queue, retry, MailKit sending
  WinSmtpRelay.Security      — TLS, DKIM, SPF, DMARC
  WinSmtpRelay.Storage       — SQLite persistence (EF Core)
  WinSmtpRelay.AdminApi      — REST API (Minimal API, class library)
  WinSmtpRelay.AdminUi       — Blazor Server admin interface (Razor Class Library)
  WinSmtpRelay.Service       — Windows Service host (Kestrel hosts API + UI)
tests/
  WinSmtpRelay.Core.Tests
  WinSmtpRelay.SmtpListener.Tests
  WinSmtpRelay.Delivery.Tests
  WinSmtpRelay.Security.Tests
  WinSmtpRelay.Integration.Tests
```

## Using the components (embedding the engine in your own host)

The relay is layered so the engine can be reused outside the bundled Windows Service — for a CLI, a
worker, or an alternate UI. The **domain contracts live in `WinSmtpRelay.Core`** (interfaces/ports,
models, configuration) and each layer exposes a single `AddRelayX()` registration. You program against the
Core interfaces and resolve the implementations from DI.

> These projects are **not published to NuGet** — consume them by project reference (work in the solution
> or a fork). The bundled `WinSmtpRelay.Service` is just one host that composes the same building blocks.

### Compose the engine

```csharp
using WinSmtpRelay.Core.Configuration;   // the *Options classes
using WinSmtpRelay.Delivery;             // AddDeliveryEngine
using WinSmtpRelay.Security;             // AddRelaySecurity
using WinSmtpRelay.SmtpListener;         // AddSmtpListener
using WinSmtpRelay.Storage;              // AddRelayStorage, RelayDbContext

var builder = Host.CreateApplicationBuilder(args);

// 1) Bind the options the engine reads (see the Service project's appsettings.json for the full set).
builder.Services.Configure<DeliveryOptions>(builder.Configuration.GetSection(DeliveryOptions.SectionName));
builder.Services.Configure<SmtpListenerOptions>(builder.Configuration.GetSection(SmtpListenerOptions.SectionName));
// … bind the other *Options you use (Dkim, Dns, EmailAuthentication, RateLimit, …) …

// 2) Compose only the layers you need.
builder.Services
    .AddRelayStorage(builder.Configuration.GetConnectionString("RelayDb") ?? "Data Source=relay.db")
    .AddRelaySecurity()     // SPF/DKIM/DMARC, DNS checks, signing, public-suffix (idempotent)
    .AddDeliveryEngine()    // outbound queue + worker (pulls in AddRelaySecurity)
    .AddSmtpListener();     // inbound SMTP (pulls in AddRelaySecurity)

var app = builder.Build();

// 3) The engine does NOT migrate the database for you — do it at startup.
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<RelayDbContext>().Database.MigrateAsync();

await app.RunAsync();
```

Pick only what you need: a **send-only** host drops `AddSmtpListener()`; a **read-only deliverability
dashboard** needs just `AddRelayStorage()` + `AddRelaySecurity()`.

### What each registration provides

| Call | Registers |
|------|-----------|
| `AddRelayStorage(connectionString)` | EF Core (SQLite) `RelayDbContext`, the runtime config cache, tenant scoping (`ICurrentTenant` / `ITenantScopeFactory`), and every persistence-backed Core service (queue, users, connectors, domains, DKIM keys, settings, readiness …). |
| `AddRelaySecurity()` | SPF/DKIM/DMARC validation, DKIM signing, inbound DKIM verification, deliverability DNS checks (`IDnsSetupService`), the local auth self-test (`IOutboundAuthCheckService`), the Public Suffix List, and the DNS resolvers. Idempotent — also pulled in by the delivery and listener engines. |
| `AddDeliveryEngine()` | Outbound queue worker, MX resolution, MailKit-based `IDeliveryService`, and the message-filter chain. |
| `AddSmtpListener()` | Inbound SMTP server, the open-relay guard, rate limiting, webhooks, and the pickup folder. |
| `AddRelayAdminAuth()` | REST API authentication (cookie + API key) and tenant-aware authorization policies. |
| `AddRelayAdminUiAuth()` + `AddBlazorBlueprintComponents()` | Blazor admin-UI authentication + the UI component library. |

### Key ports to program against (in `WinSmtpRelay.Core.Interfaces`)

- `IMessageQueue` — enqueue and inspect queued messages
- `IDeliveryService` — deliver a queued message (MX or smart host)
- `IDnsSetupService` — per-domain deliverability/DNS checks, including the synthesised DMARC-alignment outcome
- `IOutboundAuthCheckService` — the local "will this pass DMARC" self-test (build + sign + verify, no send)
- `ITenantReadinessService` — the setup-readiness checklist
- `IDkimDomainService`, `IAcceptedSenderDomainService`, `ISendConnectorService`, … — configuration CRUD
- `ICurrentTenant` / `ITenantScopeFactory` — multi-tenant scoping; **set the ambient tenant per request/operation** (everything else respects it)

### Host responsibilities (what the engine leaves to you)

Bind the `*Options` from configuration, apply EF migrations at startup, run any seeders you need, and — if
you expose the admin UI over HTTPS — provide an `IAdminCertificateProvider` (the certificate applier is a
host concern, not part of the engine).

### Out-of-process

For a UI in a separate process or language, consume the REST API (`WinSmtpRelay.AdminApi`) over HTTP with an
`X-Api-Key` (or `Authorization: Bearer`) header — see [Admin access](#admin-access). The running host serves a
machine-readable OpenAPI document at `/openapi/v1.json` (point Scalar/Swagger or a client generator at it).

## Configuration

Configuration is split between file-based and database-stored settings:

### appsettings.json (requires restart)

Infrastructure settings the application needs before it can start:

- Kestrel ports and TLS certificate paths
- SQLite database connection string
- Log levels
- Admin UI enabled/disabled, port, bind address, and HTTPS (default: HTTPS on `127.0.0.1:8025`, loopback-only)
- Windows Service settings

### SQLite database (runtime-editable via Admin UI)

Everything the admin edits during normal operation:

- Receive connectors (port/IP/TLS/auth)
- Send connectors and domain routing
- Accepted sender and recipient domains (with ownership verification)
- IP allow/deny lists
- SMTP users and credentials
- DKIM keys and per-domain config
- Rate limits and auto-ban rules
- Message filter rules
- Tenants, web-admin accounts, and API keys
- Inbound email authentication (SPF/DMARC + enforcement mode) and verification/tenant-binding policy
- DNS recommendation, backup-MX, statistics, and self-service signup settings

The Admin UI reads `appsettings.json` for display but does not write to it.

## Building

```bash
dotnet build winsmtprelay.slnx
dotnet test winsmtprelay.slnx
```

## Running

As a console app (development):

```bash
dotnet run --project src/WinSmtpRelay.Service
```

As a Windows Service:

```bash
sc.exe create WinSmtpRelay binPath="C:\path\to\WinSmtpRelay.Service.exe"
sc.exe start WinSmtpRelay
```

## Admin access

The admin UI and REST API require authentication and bind to `127.0.0.1` over HTTPS by default.

No administrator is created automatically. On first start, open the admin UI at `https://localhost:8025/account/login` — with no account yet, you are taken to **first-run setup** (`/account/initial-setup`) to define the first administrator (email + password). That account becomes the host administrator with full access. Once it exists, the setup page is closed and `/account/login` is shown normally.

If an older install still carries the legacy seeded `admin@local` account *and it is the only account*, the service removes it on start so a new administrator must be defined through first-run setup. If you ever lose admin access, re-run the installer (Repair) and tick **Reset administrator access**: on the next start all administrator accounts are removed and first-run setup runs again. To expose the admin UI beyond loopback, use the installer's network-access option (also available later via Repair), or change `AdminUi:BindAddress` deliberately / put it behind a reverse proxy. For HTTPS, the admin UI starts with a self-signed certificate; replace it on the **HTTPS Certificate** page by uploading a PFX or picking a certificate from the Windows certificate store (applied immediately, no restart). Alternatively, `AdminUi:CertificatePath` / `AdminUi:CertificatePassword` can point to a PFX on disk.

For automation, create an API key and pass it as `X-Api-Key: <key>` (or `Authorization: Bearer <key>`). Keys are scoped to a tenant and a role.

## Responsible operation

A mail relay can be abused if misconfigured. WIN-SMTP-RELAY is **closed by default**: relaying to an
external (non-hosted) recipient always requires SMTP authentication or an explicit allow-IP rule, and
this cannot be disabled by configuration — an empty config, or an overly-broad allow rule (`0.0.0.0/0`
or a near-"any" combination), will not relay to the outside world.

When operating it, also:

- **Send only solicited mail.** Stop sending to addresses that hard-bounce or complain — the per-tenant
  suppression list does this automatically; don't work around it.
- **Protect deliverability:** publish SPF/DKIM/DMARC and valid reverse DNS (PTR), use a stable (not
  dynamic) IP, and prefer a reputable **smart host** for production sending. Watch the **Health** page
  (Spamhaus / SpamCop checks) and act on any listing.
- **Protect the admin plane:** HTTPS with a real certificate, strong passwords, least-privilege API keys.

See [SECURITY.md](SECURITY.md) for the full guidance and how to report a vulnerability.

## License

[MIT](LICENSE)
