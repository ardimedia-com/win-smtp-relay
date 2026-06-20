# Introduction

WIN-SMTP-RELAY is a Windows-only, **relay-only** SMTP server: it accepts mail from internal
apps/devices and forwards it to external mail servers via MX lookup or a smart host. It is built with
.NET 10 and ships as a Windows Service with a Blazor admin UI and a REST API.

This documentation focuses on the **components** — the layered libraries the bundled service is built
from — so they can be reused outside that service.

## Layers

| Project | Responsibility |
|---------|----------------|
| `WinSmtpRelay.Core` | Domain models, **ports (interfaces)**, configuration options, the system-email composer. The stable contract everything else depends on. |
| `WinSmtpRelay.Security` | SPF/DKIM/DMARC validation and signing, deliverability DNS checks, the local authentication self-test, the Public Suffix List. |
| `WinSmtpRelay.Delivery` | Outbound queue worker, MX resolution, MailKit-based delivery, message-filter chain. |
| `WinSmtpRelay.SmtpListener` | Inbound SMTP (wraps the SmtpServer library), the open-relay guard, rate limiting, webhooks. |
| `WinSmtpRelay.Storage` | EF Core (SQLite) persistence, multi-tenant scoping, the runtime configuration cache. |
| `WinSmtpRelay.AdminApi` | REST API (Minimal API) for management and monitoring. |
| `WinSmtpRelay.AdminUi` | Blazor Server admin interface (Razor Class Library). |
| `WinSmtpRelay.Service` | The Windows Service host that composes all of the above. |

## Two ways to reuse it

- **In-process** — reference the libraries and compose the engine with the `AddRelayX()` registrations,
  then program against the [Core ports](xref:WinSmtpRelay.Core.Interfaces). See
  [Embedding the engine](embedding-the-engine.md).
- **Out-of-process** — drive a running relay from another process or language over HTTP via the
  [REST API](rest-api.md).

See [Architecture](architecture.md) for the dependency direction and the reasoning behind the layering.
