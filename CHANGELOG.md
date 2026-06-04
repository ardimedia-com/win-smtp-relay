# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Web-admin authentication: sign-in is now required for the Blazor admin UI and the REST API. Built on ASP.NET Core Identity with a tenant-aware admin account model and the roles `HostAdmin`, `TenantAdmin`, and `TenantViewer`.
- Dual authentication schemes: interactive cookie login for browsers and per-tenant hashed API keys (`X-Api-Key` header or `Authorization: Bearer`) for automation. Both produce the same role/tenant claims.
- Authorization policies `AdminView`, `AdminFull`, and `HostAdmin`: read endpoints require `AdminView`, mutating (non-GET) endpoints require `AdminFull`; `/api/health` remains anonymous.
- Initial host administrator (`admin@local`) is seeded on first run with a one-time random password written to the log (Event Log + console); the account must change its password on first sign-in.
- `Tenant` entity and a seeded default tenant; admin users and API keys are tenant-bound (foundation for multi-tenancy).
- Login, change-password, and access-denied pages plus a sign-out endpoint.
- Tenant management: host administrators can create, enable/disable, and delete tenants from a new host-only **Tenants** admin page and `/api/tenants` (gated by the `HostAdmin` policy). The default tenant cannot be deleted, and deletion is blocked while a tenant still owns data.
- Tenant switcher: a header control (host admins only) selects the tenant to work within ("All tenants" or a specific tenant). The selection — carried as an `active_tenant` claim and honored by both the request middleware and the Blazor circuit — scopes every per-tenant admin page and API call to that tenant. Blazor pages were moved to a tenant-propagating scope factory so their per-operation database scopes inherit the active tenant.
- `TenantId` on all per-tenant entities (relay users, receive/send connectors, accepted/sender domains, IP access rules, domain routes, DKIM domains, message-filter rules, queued messages, delivery logs) with a foreign key to `Tenant`; existing rows are assigned to the default tenant. Uniqueness for usernames and accepted/sender/DKIM domains is now per-tenant (composite indexes). (`RateLimitSettings` and `DailyStatistics` remain global for now — they are consumed in the background SMTP/delivery path before tenant resolution.)
- Tenant-scoped data access: admin requests (cookie or API key) and Blazor circuits are scoped to the authenticated principal's tenant via an ambient `ICurrentTenant`, enforced by an EF Core query filter on every tenant-owned entity. Host administrators see all tenants; tenant administrators see only their own tenant, and rows they create are stamped with their tenant on insert. Background services (delivery, statistics, config cache) run unscoped across all tenants.

### Changed

- The admin UI now binds to loopback (`127.0.0.1`) by default and serves over HTTPS (the ASP.NET Core development certificate in Development; configurable via `AdminUi:CertificatePath` / `AdminUi:CertificatePassword` for production). It previously bound `0.0.0.0` over plain HTTP.
- IP access rules stored in the database are now the authoritative source for relay IP authorization, evaluated with first-match Allow/Deny semantics by sort order. `Deny` rules are now honored (previously they were never evaluated), and edits made in the admin UI take effect immediately (cache invalidation). The static `SmtpListener:AllowedNetworks` list is used only as a fallback when no database rules exist.

### Fixed

- The SMTP client IP was always `null` because the listener read the wrong `ISessionContext` property key, which silently disabled the IP allow-list relay restriction, per-IP rate limiting, failed-auth auto-ban, and SPF source-IP checks (SPF fell back to loopback). The listener now uses the SmtpServer `EndpointListener.RemoteEndPointKey` constant at all call sites.
- Duplicate SMTP endpoint binding and a doubled `AllowedNetworks` list: pre-initialized collections in `SmtpListenerOptions` were appended to (not replaced) by configuration binding, producing two `0.0.0.0:25` endpoints and eight allowed networks from four. Bound collections now start empty.

### Security

- Closed the unauthenticated admin surface: the entire REST API and Blazor admin UI were previously reachable without credentials on `0.0.0.0:8025`, including user creation, configuration changes, and DKIM private-key generation.
- Closed a cross-tenant access path (IDOR): admin services/endpoints that loaded an entity by primary key used EF `Find`, which bypasses the tenant query filter. These now use filtered lookups, so a tenant administrator cannot read or modify another tenant's user, connector, domain route, DKIM domain, IP rule, message-filter rule, or queued message by guessing its id. (Blazor pages that open their own DI scope still need the ambient tenant propagated — addressed with the tenant-management UI.)
