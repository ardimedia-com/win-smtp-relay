# WIN-SMTP-RELAY

> **Work in progress** — This project is under active development and not yet ready for production use.

> **Hosted Service** — We are considering offering WIN-SMTP-RELAY as a hosted/managed service. For test environments, we would provide the service free of charge. If you are interested, please [open an issue](https://github.com/ardimedia-com/win-smtp-relay/issues) and let us know about your use case.


Open-source SMTP relay server for Windows. Built with .NET 10, designed as a modern replacement for IIS SMTP.

**Relay only** — no mailboxes, no IMAP, no POP3. Accepts mail from internal apps/devices and forwards it via MX lookup or smart host.

## Features

- Multiple receive connectors (different port/IP/TLS/auth per connector)
- Send connectors with per-domain routing (MX or smart host)
- Store-and-forward message queue (SQLite)
- STARTTLS (port 587) and implicit TLS (port 465)
- SMTP AUTH with per-user SendAs control and rate limits
- DKIM signing (per tenant), SPF/DMARC verification
- Setup page (`/setup`): live per-organization readiness checklist (can-send + deliverability) with the DNS records to publish and a test-message sender
- Health page (`/health`): live deliverability checks per sender domain and sending IP — SPF, DKIM, DMARC, MX, reverse DNS (PTR/FCrDNS), public-hostname A/AAAA, SPF coverage and 10-lookup-limit warnings, and DNSBL (Spamhaus ZEN / SpamCop), with copy-ready records
- Domain ownership verification (DNS TXT) for accepted sender and recipient domains, with optional enforcement on the SMTP path
- IP-based relay restrictions, with optional strict IP-to-tenant binding for unauthenticated submission (blocks cross-tenant sender-domain spoofing)
- Pickup folder for .eml files
- Blazor admin UI (HTTPS, loopback by default), sign-in required
- Authenticated admin access: cookie login + API keys, role-based authorization (host / tenant admin / viewer)
- Multi-tenant: isolated per-tenant configuration and data, a host-admin tenant switcher, per-tenant egress (source) IP, and optional self-service tenant signup
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

On first start the service seeds a host administrator and writes a one-time password to the log (Windows Event Log and console):

```
Username: admin@local
Password: <random, shown once>
```

Sign in at `https://localhost:8025/account/login` and change the password immediately (you are prompted to). To expose the admin UI beyond loopback, put it behind a reverse proxy or change `AdminUi:BindAddress` deliberately, and configure a real certificate via `AdminUi:CertificatePath` / `AdminUi:CertificatePassword`.

For automation, create an API key and pass it as `X-Api-Key: <key>` (or `Authorization: Bearer <key>`). Keys are scoped to a tenant and a role.

## License

[MIT](LICENSE)
