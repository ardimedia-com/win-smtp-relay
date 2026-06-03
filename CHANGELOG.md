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

### Changed

- The admin UI now binds to loopback (`127.0.0.1`) by default and serves over HTTPS (the ASP.NET Core development certificate in Development; configurable via `AdminUi:CertificatePath` / `AdminUi:CertificatePassword` for production). It previously bound `0.0.0.0` over plain HTTP.

### Fixed

- The SMTP client IP was always `null` because the listener read the wrong `ISessionContext` property key, which silently disabled the IP allow-list relay restriction, per-IP rate limiting, failed-auth auto-ban, and SPF source-IP checks (SPF fell back to loopback). The listener now uses the SmtpServer `EndpointListener.RemoteEndPointKey` constant at all call sites.

### Security

- Closed the unauthenticated admin surface: the entire REST API and Blazor admin UI were previously reachable without credentials on `0.0.0.0:8025`, including user creation, configuration changes, and DKIM private-key generation.
