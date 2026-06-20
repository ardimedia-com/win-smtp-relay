---
_layout: landing
---

# WIN-SMTP-RELAY

Open-source SMTP relay server for Windows, built with .NET 10 as a modern replacement for IIS SMTP.
**Relay only** — no mailboxes, no IMAP, no POP3: it accepts mail from internal apps/devices and forwards
it via MX lookup or a smart host.

This site documents the **reusable components** behind the relay so you can embed the engine in your own
host (a CLI, a worker, or an alternate UI) and program against its .NET API.

## Where to start

- [Introduction](articles/introduction.md) — what the relay is and how the layers fit together.
- [Embedding the engine](articles/embedding-the-engine.md) — compose the engine in your own host with the
  `AddRelayX()` registrations and the Core ports.
- [Architecture](articles/architecture.md) — the layering, dependency direction, and reuse contracts.
- [REST API](articles/rest-api.md) — drive the relay from another process over HTTP.
- [API reference](xref:WinSmtpRelay.Core.Interfaces) — the generated .NET API documentation for the component libraries (also in the **API** tab).

> The component libraries are not published to NuGet — consume them by project reference from the
> [repository](https://github.com/ardimedia-com/win-smtp-relay) or a fork.
