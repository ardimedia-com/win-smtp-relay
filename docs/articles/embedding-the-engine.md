# Embedding the engine

The relay is layered so the engine can be reused outside the bundled Windows Service — for a CLI, a
worker, or an alternate UI. The domain contracts live in `WinSmtpRelay.Core` and each layer exposes a
single `AddRelayX()` registration. You program against the Core interfaces and resolve the
implementations from DI.

> The component libraries are **not published to NuGet** — consume them by project reference (work in the
> solution or a fork).

## Compose the engine

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

## What each registration provides

| Call | Registers |
|------|-----------|
| `AddRelayStorage(connectionString)` | EF Core (SQLite) `RelayDbContext`, the runtime config cache, tenant scoping (`ICurrentTenant` / `ITenantScopeFactory`), and every persistence-backed Core service (queue, users, connectors, domains, DKIM keys, settings, readiness …). |
| `AddRelaySecurity()` | SPF/DKIM/DMARC validation, DKIM signing, inbound DKIM verification, deliverability DNS checks (`IDnsSetupService`), the local auth self-test (`IOutboundAuthCheckService`), the Public Suffix List, and the DNS resolvers. Idempotent — also pulled in by the delivery and listener engines. |
| `AddDeliveryEngine()` | Outbound queue worker, MX resolution, MailKit-based `IDeliveryService`, and the message-filter chain. |
| `AddSmtpListener()` | Inbound SMTP server, the open-relay guard, rate limiting, webhooks, and the pickup folder. |
| `AddRelayAdminAuth()` | REST API authentication (cookie + API key) and tenant-aware authorization policies. |
| `AddRelayAdminUiAuth()` + `AddBlazorBlueprintComponents()` | Blazor admin-UI authentication + the UI component library. |

## Key ports to program against

In `WinSmtpRelay.Core.Interfaces`:

- `IMessageQueue` — enqueue and inspect queued messages
- `IDeliveryService` — deliver a queued message (MX or smart host)
- `IDnsSetupService` — per-domain deliverability/DNS checks, including the synthesised DMARC-alignment outcome
- `IOutboundAuthCheckService` — the local "will this pass DMARC" self-test (build + sign + verify, no send)
- `ITenantReadinessService` — the setup-readiness checklist
- `IDkimDomainService`, `IAcceptedSenderDomainService`, `ISendConnectorService`, … — configuration CRUD
- `ICurrentTenant` / `ITenantScopeFactory` — multi-tenant scoping; **set the ambient tenant per request/operation** (everything else respects it)

## Host responsibilities

The engine leaves these to the host: bind the `*Options` from configuration, apply EF migrations at
startup, run any seeders you need, and — if you expose the admin UI over HTTPS — provide an
`IAdminCertificateProvider` (the certificate applier is a host concern, not part of the engine).
