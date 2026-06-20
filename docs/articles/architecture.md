# Architecture

## Dependency direction

The domain contracts live in `WinSmtpRelay.Core` (interfaces/ports, models, configuration). Every other
layer depends **inward** on Core; Core depends on nothing but the BCL. Infrastructure implements the
ports Core defines — the dependency arrow never points from Core out to infrastructure.

```
            ┌─────────────────────────────────────────────┐
            │            WinSmtpRelay.Core                 │
            │   models · ports (interfaces) · options      │
            └─────────────────────────────────────────────┘
                 ▲          ▲          ▲          ▲
        ┌────────┘   ┌──────┘    ┌─────┘     ┌────┘
   ┌─────────┐  ┌──────────┐ ┌──────────┐ ┌──────────────┐
   │ Storage │  │ Security │ │ Delivery │ │ SmtpListener │
   └─────────┘  └──────────┘ └────┬─────┘ └──────┬───────┘
                     ▲             │ pulls        │ pulls
                     └─────────────┴──────────────┘
                          (Delivery & SmtpListener
                           depend on Security)

   ┌──────────┐  ┌──────────┐        ┌──────────────────────┐
   │ AdminApi │  │ AdminUi  │   ◀──  │ WinSmtpRelay.Service  │
   └──────────┘  └──────────┘  host  │  (composition root)   │
                                      └──────────────────────┘
```

## Composition

Each layer registers only its own types through an `AddRelayX()` extension method, and pulls in the layers
it depends on (idempotently, via `TryAdd`). The host composes the engine from these — see
[Embedding the engine](embedding-the-engine.md). `WinSmtpRelay.Service` is one such host; another host
(CLI, worker, alternate UI) composes the same building blocks.

Host-specific concerns stay in the host, not the engine — for example the admin-UI HTTPS certificate
applier depends on a host-provided `IAdminCertificateProvider`, so it is registered by the host rather
than by `AddRelaySecurity()`.

## Reuse contracts

- **In-process** — reference the libraries, compose with `AddRelayX()`, and call the
  [Core ports](xref:WinSmtpRelay.Core.Interfaces).
- **Out-of-process** — call the [REST API](rest-api.md) over HTTP.

## Multi-tenancy

The engine is multi-tenant. `ICurrentTenant` is the ambient tenant for query filtering and
`ITenantScopeFactory` creates a DI scope bound to a tenant. A host must set the ambient tenant per
request/operation; persistence-backed services then apply the tenant filter automatically.
