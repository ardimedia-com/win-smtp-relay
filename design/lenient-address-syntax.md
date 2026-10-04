# Design: Lenient MAIL FROM / RCPT TO Syntax

> Status: **Implemented** 2026-10-04 (1.0.0-beta1-build73). Verified against the SmtpServer v11.1.0
> source; the reference floats on `11.*`, so `LenientAddressSyntaxEndToEndTests` is what keeps the
> claims below true.

## Problem

RFC 5321 requires `MAIL FROM:<user@host>` and `RCPT TO:<user@host>`. A lot of embedded firmware sends
`MAIL FROM: user@host` instead — a blank after the colon and no angle brackets. The firmware cannot be
changed.

Observed live: two HP MSA2012 storage controllers (10.1.1.40/41) sent
`MAIL FROM: stodat03@ardimedia.com` for 2½ months. The SMTP library answered `502 Unrecognized command`
— not even a 501 — and the controllers retried every few minutes: about 64,000 refused attempts, all
recorded as `CommandSyntaxError` on the Rejections page (which is how this was found), and not one
alert mail delivered.

The library already tolerates the blank (it skips spaces after the colon); what it refuses is the bare
address. `TryMakeMail` fails, its 501 is discarded, and the parser falls through to "unrecognized".

## Decision

Accept a bare address — optionally padded with blanks around the colon, optionally followed by ESMTP
parameters — **for clients from a trusted network only**, by rewriting that one command line into the
canonical form before the library's parser sees it:

```
MAIL FROM: device@example.com SIZE=123   →   MAIL FROM:<device@example.com> SIZE=123
RCPT TO: support@example.com             →   RCPT TO:<support@example.com>
```

Syntax only. The rewritten line goes through the unchanged parser, the state machine and every gate in
`RelayMailboxFilter` (allowed networks, tenant attribution, open-relay protection, rate limits, sender
policies). Nothing is authorized by this change.

**Trusted** is `IpAccessEvaluator.IsTrustedSource` — the same "configured network" classification the
Rejections page uses (a specific-enough Allow rule, or the static `AllowedNetworks` when no DB rules
exist). The devices the Rejections page flags as misconfigured are therefore exactly the devices this
tolerance covers. Everyone else stays strict and still gets the 502, recorded as before: port 25 on the
internet gets no new parser surface.

## Mechanism and why

SmtpServer v11 offers no parser extension point:

| Candidate | Why not |
|---|---|
| Subclass `SmtpParser`, override `TryMakeMail` / `TryMakePath` | `SmtpSession` constructs `new SmtpParser(_commandFactory)` itself; a subclass is never used. |
| Custom `ISmtpCommandFactory` | Called only AFTER a successful parse, with an already-parsed `IMailbox`. A line the parser rejects never reaches it. |
| Fork / patch the library | Maintenance cost out of all proportion to a one-line rewrite. |
| **Custom `IEndpointListenerFactory`** | **Chosen.** A documented, replaceable service (`ServiceProvider.Add(IEndpointListenerFactory)`), and the only one that sees the raw bytes before the parser. |

`LenientEndpointListenerFactory` derives from the library's own `EndpointListenerFactory` and keeps its
listener, so TCP accept, the `EndpointListener:RemoteEndPoint` property and TLS are all still the
library's. It only wraps the session's pipe; the wrapped `Input` is a `PipeReader` that substitutes the
command line at the head of the buffer when it is a sloppy MAIL / RCPT from a trusted client.

Properties the reader guarantees:

- **No read-ahead.** Every read is the library's own read. STARTTLS depends on nothing being buffered
  beyond what the library would buffer; after an upgrade the library swaps its reader and the wrapper
  re-wraps the new one, so TLS sessions get the same treatment.
- **One line at a time.** A substituted read holds exactly the rewritten line plus CRLF; consuming it
  advances the inner reader past the original line. Pipelined commands behind it are untouched and are
  each inspected when they reach the head of the buffer.
- **Command lines only.** Bytes read while a command executes — the DATA body, AUTH continuation lines
  — pass through byte for byte. The window is tracked with the session's `CommandExecuting` /
  `CommandExecuted` events, and closed by `ResponseException` too, because a command that throws never
  raises `CommandExecuted`.
- **Lazy, fail-closed trust.** The trust lookup runs only when a sloppy line actually arrives (ordinary
  sessions and the accept loop never pay for it), is cached per session, and any failure means strict.

## Interaction with observable rejections

Unchanged. A line from an untrusted client, or one the normaliser does not match, reaches the parser
as-is; its failure raises `ResponseException` with `SmtpSession:Buffer` holding the original line, and
`SmtpRelayServer.OnResponseException` records it as before. Only a line that was rewritten and still
fails (e.g. an invalid mailbox inside it) carries the normalised text in its buffer.

Existing `CommandSyntaxError` rows for such devices stop counting once the update is installed; they
age out of the 24 h window like any fixed finding.

## Out of scope

- Display names (`MAIL FROM: Device <a@b.c>`), half-bracketed or `@`-less addresses: not the observed
  quirk, left to the strict parser.
- A per-connector or UI switch. The tolerance is narrow, trusted-only and authorizes nothing; add a
  switch only if a real need appears.
