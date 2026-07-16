# Design: Observable Rejections

> Status: **Implemented** — recording, self-check finding, daily-report section, the Rejections page and
> the one-click actions are all in. Written, revised, decided and implemented 2026-07-16.
>
> The revision replaced the original mechanism. The first draft claimed a single library hook
> (`ISessionContext.ResponseException`) observes every rejection. Verified against the SmtpServer
> v11.1.0 source, that is false: the hook does not see a single one of the relay's own policy
> rejections. The problem statement below is unchanged and still stands; the mechanism is rebuilt
> around what the library actually does. See "Why the obvious mechanism does not work".

## Problem

A relay's most dangerous state is silently not relaying.

Accepted mail is first-class today: it enters the queue, it gets a Journal entry, per-recipient
delivery outcomes, a resend action, bounce statistics and a daily report. **Rejected mail is an
`ILogger` call.** A device that the relay refuses is invisible unless an operator thinks to grep the
Windows Event Log — and nobody greps a log they have no reason to suspect.

That asymmetry is backwards. A rejected submission is the product failing at its one job, and it is
strictly *more* actionable than a bounce: a bounce is usually the remote side's problem, a rejection
is always ours.

The failure mode is silent on both ends. The relay logs a warning nobody reads; the sending device
reports a generic error and keeps retrying forever. Neither side escalates. A misconfigured device
can sit in this state indefinitely, and the longer it does, the less likely anyone connects the
missing mail to the relay.

## Current behaviour

| Path | Where it surfaces today |
|---|---|
| Message accepted, delivered | Journal, statistics, daily report |
| Message accepted, bounced | Journal, bounce rate, suppression list, daily report |
| **Message rejected at the SMTP session** | **Event Log warning only — and not even always** |

`RelayMailboxFilter` (`src/WinSmtpRelay.SmtpListener/RelayMailboxFilter.cs`) has **15** reject paths,
not the nine the first draft listed. Every one answers the client **`550 mailbox unavailable`**:

`CanAcceptFromAsync`:

| # | Line | Reason | Logs? |
|---|---|---|---|
| 1 | :51 | message size exceeds `MaxMessageSizeBytes` | yes — but client IP not yet resolved (read at :54) |
| 2 | :84 | unauthenticated tenant attribution failed (`AmbiguousIp` / `CrossTenantDomain` / `Unresolved`) | yes |
| 3 | :98 | tenant disabled | yes |
| 4 | :105 | IP auto-banned | yes |
| 5 | :111 | per-IP rate limit exceeded | **no** — logged by `RateLimiter.cs:66` |
| 6 | :126 | blocked by IP access rules | yes |
| 7 | :134 | not in allowed networks | yes |
| 8 | :141 | per-sender rate limit exceeded | **no** — logged by `RateLimiter.cs:91`/`:98` |
| 9 | :156 | sender domain not in accepted sender domains | yes |
| 10 | :169 | sender domain ownership not verified | yes |
| 11 | :192 | user not allowed to send as this address (SendAs) | yes |
| 12 | :200 | per-user rate limit exceeded | yes |
| 13 | :221 | SPF hard fail in Reject mode | yes |

`CanDeliverToAsync`:

| # | Line | Reason | Logs? |
|---|---|---|---|
| 14 | :261 | recipient domain ownership not verified | yes — client IP not resolved at this point (read at :275) |
| 15 | :290 | open-relay protection: external relay without auth or explicit allow rule | yes |

None produce a queryable record, a UI surface, or a daily-report entry. Two produce nothing in this
class at all. There is **no reject-reason type** anywhere in the codebase — every reason exists only
as free text inside a log template.

**And they are not the whole picture.** A malformed `MAIL FROM` fails in the library's parser, before
`RelayMailboxFilter` runs — so it produces *no log line at all*. A device sending a syntactically
invalid envelope is invisible even to an operator who does read the Event Log. That class needs a
different mechanism than the fifteen above, which is the whole reason this design has two halves.

## Why the obvious mechanism does not work

`ISessionContext.ResponseException` looks like the answer. It exists, it carries everything useful,
and it fires before the reply is written. It is also the wrong hook, and the reason is worth writing
down so nobody rediscovers it the expensive way.

The library rejects in **two structurally different places**, and only one of them throws.

**The state machine throws** — `src/SmtpServer/SmtpSession.cs:73`:

```csharp
if (_stateMachine.TryAccept(command, out var errorResponse) == false)
{
    throw new SmtpResponseException(errorResponse);
}
```

**The mailbox filter does not** — `src/SmtpServer/Protocol/MailCommand.cs:63-76`:

```csharp
switch (await container.Instance.CanAcceptFromAsync(context, Address, size, cancellationToken))
{
    case true:
        context.Transaction.From = Address;
        await context.Pipe.Output.WriteReplyAsync(SmtpResponse.Ok, cancellationToken);
        return true;

    case false:
        await context.Pipe.Output.WriteReplyAsync(SmtpResponse.MailboxUnavailable, cancellationToken);
        return false;          // straight to the pipe — no exception, ever
}
```

`RcptCommand.cs:44-52` is identical. `RaiseResponseException` has exactly two call sites, both in
`SmtpSession.cs` (`:87`, `:95`), both inside `catch (SmtpResponseException)`. No exception, no event.

So the original claim — *"There is no 5xx exit that bypasses `RaiseResponseException`"* — inverted the
picture. It is true that state-machine rejections, parser failures and timeouts all travel through the
hook. It does not follow that our rejections do, because **our rejections are not state-machine
rejections**. `RelayMailboxFilter` runs inside `MailCommand.ExecuteAsync`, downstream of the state
machine, on a code path that writes its reply directly. `IMailboxFilter` returns `bool`; a `bool`
cannot carry a reason, and `MailCommand` maps `false` to a hard-coded constant.

### Coverage map (v11.1.0, verified)

| Exit | Reply | Hook fires? |
|---|---|---|
| Parser failure (`SmtpParser.TryMake == false`) — **carries the raw buffer** | varies | **yes** — `SmtpSession.cs:130` |
| State machine rejects command (wrong sequence) | varies | **yes** — `SmtpSession.cs:75` |
| Unparseable mailbox (`CreateMailbox` → null) | 553 | **yes** — `SmtpParser.cs:1102`/`:1108` |
| Command wait timeout / session cancelled | 421 | **yes** — `SmtpSession.cs:144`/`:147` |
| DATA exceeds max message size | 552 | **yes** — `PipeReaderExtensions.cs:42` |
| AUTH retries exhausted (session closes) | 421 | **yes** — `AuthCommand.cs:80` |
| **All 15 `RelayMailboxFilter` gates** | **550** | **no** — `MailCommand.cs:72`, `RcptCommand.cs:51` |
| **Authentication failed** | 535 | **no** — `AuthCommand.cs:53`/`:61`/`:114` |
| AUTH required before MAIL | 530 | **no** — `MailCommand.cs:43` |
| `SIZE=` parameter over the library limit | 552 | **no** — `MailCommand.cs:56` |
| DATA with no valid recipients | 554 | **no** — `DataCommand.cs:36` |
| `RelayMessageStore.SaveAsync` returns a non-OK response, or throws | varies / 554 | **no** — `DataCommand.cs:59`/`:67` |

The hook covers the class an operator least expects and misses the class the feature exists for. It
is still worth having — it is the *only* way to see the malformed-envelope case, and it is the only
source of the raw command buffer — but it is a supplement, not the mechanism.

**This map is now enforced, not just documented.** `RejectedSubmissionsEndToEndTests` drives a real
listener over a real socket: one test sends a malformed `MAIL FROM` and asserts a `CommandSyntaxError`
row carrying the raw line (the hook fires), the other trips a policy gate and asserts **exactly one**
row with the gate's own reason (the hook stayed silent — a second, protocol-classified row would mean
the premise had changed). Since the package reference floats on `11.*`, those tests are what will say
so if a future version moves an exit from one column to the other.

## Consequence: two recording sources, one store

1. **Policy source — `RelayMailboxFilter`.** Each of the 15 gates records its own rejection with a
   typed reason. This is where the tenant, sender, client IP and the exact gate are known, and it is
   the only place they are knowable. There is no way to observe these from outside; the effort the
   first draft hoped to avoid is unavoidable.
2. **Protocol source — the `ResponseException` hook.** Attached in the existing `SessionCreated`
   lambda (`src/WinSmtpRelay.SmtpListener/SmtpRelayServer.cs:133`). Covers parser/syntax failures,
   and supplies the raw failing command line via `Properties["SmtpSession:Buffer"]`.

   The buffer is stored only after **redaction** (decided 2026-07-16). The exposure is real but
   narrow: the buffer is populated *only* on parser failure, so what leaks is a **malformed**
   `AUTH PLAIN <base64>` line — a well-formed AUTH that merely fails authentication throws nothing
   and carries no buffer, and `AUTH LOGIN` sends its credentials on continuation lines read by
   `ReadBase64EncodedLineAsync`, bypassing the command parser entirely. The rule is fail-safe by
   prefix, not fail-open by pattern: a line starting with `AUTH` (case-insensitive) is reduced to
   verb + mechanism (`AUTH PLAIN [redacted]`); no regex over the base64 payload. Everything else is
   capped at **512 bytes**, with non-printable / non-UTF-8 bytes escaped before storage.

Both write to one aggregation store, so the report and the UI have a single table regardless of which
half saw the rejection.

To keep the 15 gates from turning into 15 copies of the same recording block, the practical shape is a
single private helper — `Reject(context, RejectReason.X, …)` returning `false` — so each gate stays a
one-line `return Reject(...)`. That also fixes the two gates that currently log nothing and the two
that reject before the client IP is resolved (gate 1 at :51 and gate 14 at :261 — the IP read must
move above them).

### The reason model and store (decided 2026-07-16)

`RejectReason` (enum, `WinSmtpRelay.Core`) — one member per gate plus the protocol-level classes. The
temporary/permanent split the 550 defect (below) demands is carried by the member; the rate limits
are the temporary ones:

```csharp
public enum RejectReason
{
    // CanAcceptFromAsync (gates 1-13)
    MessageTooLarge,
    TenantAttributionFailed,     // Detail carries AmbiguousIp / CrossTenantDomain / Unresolved
    TenantDisabled,
    IpAutoBanned,
    IpRateLimitExceeded,         // temporary
    IpBlocked,                   // by IP access rules
    NotInAllowedNetworks,
    SenderRateLimitExceeded,     // temporary
    SenderDomainNotAccepted,
    SenderDomainNotVerified,
    SendAsDenied,
    UserRateLimitExceeded,       // temporary
    SpfFail,
    // CanDeliverToAsync (gates 14-15)
    RecipientDomainNotVerified,
    OpenRelayDenied,
    // Protocol source (ResponseException hook)
    CommandSyntaxError,          // parser failure — the only reason that carries a buffer
    CommandSequenceError,        // state machine
    InvalidMailboxName,
    ProtocolOther
}
```

`RejectedSubmission` (entity) — the aggregate row per requirement 3: `Id`, `TenantId?` (nullable —
the `AdminMembership` pattern: null = host-level, **not** `ITenantOwned`, excluded from the global
query filter, queried explicitly), `ClientIp`, `Reason`, `ReplyCode`, `SenderDomain?`, `Detail?`,
`Count`, `FirstSeenUtc`, `LastSeenUtc`, `LastBuffer?` (redacted, capped), `IsTrustedSource`.

`IsTrustedSource` is evaluated and stamped **at record time**, not derived at query time — the
eviction partition (requirement 5) and the health check must not re-evaluate today's rules against
historical rows, and a rule edit must not silently reclassify history.

This model also settles what was open question 8 in the first draft: rate-limit rejections are not a
separate category — they are distinct, temporary-marked members in the same table.

### Constraints found in the code

- **DI is not reachable from the session.** `args.Context.ServiceProvider` is SmtpServer's own
  `ComponentModel.ServiceProvider`, populated with exactly three objects
  (`SmtpRelayServer.cs:125-131`). The hook must close over services held by `SmtpRelayServer`.
  `RelayMailboxFilter` is a singleton and already injects `IRuntimeConfigCache` plus an
  `IServiceScopeFactory` for scoped work (`RelayMailboxFilter.cs:311`) — the recorder follows that.
- **No batching infrastructure exists.** There is no `Channel`, queue or batching writer anywhere in
  `src`. Requirement 5 has nothing to reuse and must be built. The closest shape to copy is
  `QueueDepthRecorder` (`src/WinSmtpRelay.Storage/QueueDepthRecorder.cs`): a singleton that is also a
  `BackgroundService`, triple-registered (`Program.cs:180-182`).
- **The library version stays floating — by decision (owner, 2026-07-16).**
  `WinSmtpRelay.SmtpListener.csproj:14` references `SmtpServer` as `Version="11.*"`, and it stays
  that way rather than pinning to 11.1.0. Everything in the coverage map above is a statement about
  11.1.0's internals, and `BufferKey` is a private const — so the implementation must absorb the
  drift risk: the buffer read is **best-effort behind a try-get** (the record stays useful without
  it, per the redaction note above), and the coverage map must be **re-verified whenever the
  resolved version changes**, because a future 11.x can silently alter which exits raise the event,
  not only drop the buffer.

## Adjacent defect: every policy rejection is a permanent 550

`SmtpResponse.MailboxUnavailable` is `SmtpReplyCode.MailboxUnavailable = 550` — a **permanent**
failure. All 15 gates answer with it, including gates 5, 8 and 12: the rate limits.

A correctly-implemented sending MTA treats 550 as final. It does not retry; it bounces the message
back to its sender. So a legitimate sender that is merely being **throttled** does not get delayed —
it gets its mail rejected for good. Throttling is by definition temporary and belongs in 4xx
(`451`/`452`), which instructs the client to retry later.

This is a defect in its own right, independent of this design, and it constrains it: the reason model
must distinguish "temporary, retry later" from "permanent, fix your configuration", because they are
different findings for the operator and different replies for the device. Fixing the reply codes,
however, requires changing what `IMailboxFilter` can express — `bool` cannot carry a code. That is a
separate decision (see open questions).

Two smaller inconsistencies in the same area: a size rejection answers 550 when our gate 1 trips but
552 when the library's own check trips (`MailCommand.cs:56`), because there are two independent size
limits (`SmtpListenerOptions.MaxMessageSizeBytes` and `ServerOptions.MaxMessageSizeOptions`); and
`AUTH` failures answer 535 on a path no hook observes.

## Requirements

1. **Record every rejection the relay returns** — all 15 policy gates *and* the protocol-level
   failures the hook sees. Neither source alone is sufficient.
2. **Carry a typed reason.** The aggregation key presumes a `reason` that does not exist yet. It
   cannot be recovered from the wire (all 15 gates answer the same 550) and it cannot be recovered
   from the hook (which never fires for them). It has to be introduced at the gate — the decided type
   is `RejectReason` (see "The reason model and store").
3. **Aggregate, do not append.** Upsert on `(client IP, reason, reply code, sender domain)` with
   `Count`, `FirstSeenUtc`, `LastSeenUtc` and the most recent raw buffer. A device retrying every 30
   minutes for six months is one row with a large count, not 8,000 rows.
   - **The sender domain is part of the record, not an optional extra.** The one-click offer (below)
     acts on "IP X wants to send as domain Y" — a row keyed only on `(IP, reason)` cannot power
     `[Accept domain]`, and a device alternating between two sender domains is two different
     requests. The first draft specified the row without the domain while simultaneously promising a
     feature that needs it. Where no sender is knowable (parser-level rejects), the field is **empty,
     not null**: ANSI/SQLite treat NULLs as distinct in a unique index, so a nullable key column
     would let every protocol-level reject insert a fresh row and silently defeat the aggregation —
     and a null-to-`""` value converter cannot paper over it, because EF Core never passes null to a
     converter. The same goes for whatever context `[Bind IP to tenant]` needs (the attribution
     outcome).
   - **`FirstSeenUtc` resets after a gap.** "Recurring for > 24 h" is `LastSeen − FirstSeen`; a
     device that was fixed and breaks again weeks later must not inherit its stale `FirstSeenUtc`
     and trigger an instant warning. On upsert, when `now − LastSeenUtc` exceeds a reset window
     (e.g. 48 h), treat the row as a fresh episode and reset `FirstSeenUtc` (keeping the lifetime
     `Count` is fine).
4. **Separate signal from noise.** Port 25 accepts from anywhere the listener is exposed to; with
   parser-level rejects included, every scanner speaking broken protocol lands in this table.
   - Reject from **within a trusted network** → a configured device is misconfigured → **finding**.
   - Reject from anywhere else → the relay working as designed → count silently, never alert.

   **"Trusted" is decided (owner, 2026-07-16) as the tenant-agnostic evaluation** — at parser level,
   and in the attribution-failure case, there is no tenant, so nothing narrower is computable. An IP
   is trusted when it matches **any** DB allow rule, or (only when no DB rules exist, mirroring the
   fallback semantics at `RelayMailboxFilter.cs:114-116`) the static `AllowedNetworks` list —
   counting only rules that pass the breadth guards the relay gate already applies
   (`IpAccessEvaluator.IsTooBroadForRelay`: minimum /8 IPv4 / /16 IPv6, any-network excluded), so a
   single broad allow rule cannot mark the whole internet trusted. No ready-made service answers
   this — `IpAccessEvaluator` is static and per-tenant — so a small tenant-agnostic helper is part
   of the work. The verdict is stamped on the row as `IsTrustedSource` at record time.
5. **Bounded — and eviction must be partitioned by trust.** Cap distinct rows; retention follows the
   existing `DataRetention` profile, enforced by `RetentionService`
   (`src/WinSmtpRelay.Storage/RetentionService.cs`) in the nightly `StatisticsAggregator` pass. But
   eviction by `LastSeenUtc` alone is exploitable: a scanner rotating source IPs mints unlimited
   fresh untrusted rows, and pure recency eviction would let that churn displace exactly the
   trusted-network findings the feature exists for. Cap the two populations separately, or always
   evict untrusted rows first — untrusted pressure must never evict a trusted row. Two SQLite
   translation constraints apply to the retention query: the cutoff column must be a
   **non-nullable** `DateTimeOffset`, and the table must be orderable — the `DateTimeOffset`
   convention (`RelayDbContext.cs:51-70`) makes that work.
6. **Cheap on the hot path.** Both sources run inside an SMTP session; a rejection must never slow
   the listener or, worse, fail a session. Because the store is an aggregate (requirement 3), the
   right shape is **not** a write-batching queue but **in-memory aggregation with periodic flush**:
   the hot path upserts a `ConcurrentDictionary` keyed like the table (memory-only, no await, no
   DB), and a background loop folds the dictionary into SQLite every few seconds — the
   `QueueDepthRecorder` shape (singleton + `BackgroundService`, triple-registered,
   `Program.cs:180-182`). Losing a few seconds of counts on a crash is acceptable for a statistics
   table. A scanner storm then hits RAM, not the database, and the DB write rate stays bounded by
   the flush interval regardless of reject volume.
7. **Tenant-scoped where a tenant is resolvable, host-level where it is not.** Attribution failures
   are precisely the case where no tenant is known. The codebase already has the pattern for this:
   `AdminMembership` uses a **nullable `TenantId`**, is deliberately **not** `ITenantOwned`, is
   excluded from the global query filter and is queried explicitly (`RelayDbContext.cs:108-125`).

## Surfacing

The health-check architecture carries this for free. A `RejectedSubmissionsHealthCheck :
HealthCheckBase` added to `HealthCheckRegistration` (`src/WinSmtpRelay.Service/HealthChecks/HealthCheckRegistration.cs:23`,
one `AddScoped` line) is picked up by the daily self-check report and `/diagnostics` without further
work; `HealthFinding`, `HealthSeverity` and `HealthCategories` already exist.

| Condition | Severity |
|---|---|
| Single rejection from a trusted network | Info |
| **Same (IP, reason) recurring for > 24 h from a trusted network** | **Warning** |
| Rejections from untrusted sources | not a finding; counted only |

The 24-hour rule is the important one. A one-off rejection is noise — a wrong password, a test, a
transient. A device that has been failing for a day is, by definition, configured wrong and not
self-correcting. That is the alert worth having, and it is the one that turns "discovered by
accident, months later" into "reported the next morning".

"The next morning" is literal, and matches the mechanism: `HealthCheckService.MaybeAlertOnNewErrorsAsync`
sends an immediate mail only for newly-appeared findings at severity **Error**. A **Warning** appears
in the daily digest (`ReportingService.cs:243-277` lists findings `>= Warning`) and nowhere else. If a
day-long outage should page immediately, the severity must be Error, not Warning.

Findings are diffed across runs on `Code|Target` (`HealthCheckService.cs:152`), so `Target` should
carry the aggregation identity (the client IP, or IP + reason) for the diff to behave.

Additionally: a UI page or dashboard card listing current rejected senders, and inclusion in the daily
status email — a new `AppendXAsync` alongside `AppendHealthSectionAsync` in `BuildDigestAsync`.

## The idea that makes it a feature

**A rejection from a trusted network is not an error. It is a request.**

> `10.x.x.x` wants to send as `device@example.com` — rejected: domain not in accepted sender domains.
> **[Accept domain] [Bind IP to tenant] [Ignore]**

One click turns a silent failure into an onboarding step. The relay already knows everything needed to
make the offer: it has the client IP, the sender domain, the tenant rules and the exact gate that
refused. It simply throws that context away today.

This is the difference between a log viewer and a feature, and it fits the direction the setup page
already takes: a readiness checklist that tells the operator what to do, rather than an error that
tells them something went wrong.

Note that this pay-off depends entirely on requirements 2 and 3. The one-click offer is only possible
because the record names *which gate* refused (the typed reason) and *what the device was trying to
do* (the sender domain in the row). A table of "550 mailbox unavailable" rows cannot offer anything.
Neither field is bookkeeping — together they are the feature.

## Non-goals

- Not a replacement for the Event Log. Keep the `LogWarning` calls.
- Not a security/IDS product. Untrusted-source rejections are counted, not analysed or alerted on.
- Not message capture. Record the failing command line, never message bodies.
- Not automatic remediation. The one-click accept is operator-initiated, always.

## Decided (owner, 2026-07-16)

The four formerly-blocking questions are resolved; their substance lives in the body above:

- **Reason model** — `RejectReason` enum + `RejectedSubmission` entity ("The reason model and store").
- **"Trusted network"** — tenant-agnostic, breadth-guarded, stamped at record time (requirement 4).
- **SmtpServer version** — stays floating `11.*`; buffer read best-effort, coverage map re-verified
  on version change ("Constraints found in the code").
- **Buffer redaction** — AUTH-prefix fail-safe, 512-byte cap, escaping (protocol source, "Consequence"
  section).

## What shipped

| Piece | Where |
|---|---|
| Reason model + aggregate row | `Core/Models/RejectedSubmission.cs`, migrations `AddRejectedSubmissions`, `AddRejectedSubmissionIgnored` |
| Hot-path fold + periodic flush + capped, trust-partitioned eviction | `SmtpListener/RejectionRecorder.cs` |
| Policy source (15 gates → `Reject(...)`) | `SmtpListener/RelayMailboxFilter.cs` |
| Protocol source (the hook) + reply-code classification | `SmtpListener/SmtpRelayServer.cs` |
| Buffer redaction | `SmtpListener/RejectionBuffer.cs` |
| Trust classification | `SmtpListener/IpAccessEvaluator.IsTrustedSource` |
| 24 h finding + remediation text | `Service/HealthChecks/Checks/RejectedSubmissionsHealthCheck.cs` |
| Daily-report section | `Service/ReportingService.cs` |
| Age-based pruning (on `DeliveryLogDays`, by `LastSeenUtc`) | `Storage/RetentionService.cs` |
| The page + one-click actions | `AdminUi/Components/Pages/Rejections.razor`, nav in `MainLayout.razor` |
| Coverage-map proof, redaction, trust classification | `RejectedSubmissionsEndToEndTests`, `RejectionBufferTests`, `IpAccessEvaluatorTrustedSourceTests` |

**UI placement (was open question 4): the Monitor group, next to the Journal.** That group is already
defined as "meaningful in any scope — host = aggregate across all tenants, tenant = scoped", which is
exactly this table's semantics, and the design's own problem statement is the asymmetry between accepted
and refused mail. Refused mail belongs beside accepted mail, not in a diagnostics corner.

Two consequences of the row NOT being `ITenantOwned`, both handled in the page and worth remembering
before anyone writes a second reader:

- **There is no query filter**, so the tenant split is applied by hand. Forgetting it shows every tenant
  their neighbours' rejections.
- **In host scope the DbContext neither filters nor stamps**, so a create would silently land in the
  Default tenant. The actions therefore require a tenant in scope; the page stays readable without one
  (and that is the only place the attribution-failure rows — which have no tenant — are visible at all,
  which settles open question 3 conservatively: host admins only).

`[Ignore]` needed state (`IgnoredUtc`) rather than a delete: the device's next attempt would recreate the
row, so a deleted rejection is not a dismissed one. Ignoring suppresses the finding and the report but
keeps counting, so the data is still there when someone asks why a device never sent anything.

Values chosen while building (open question 2, non-blocking, revisit if they chafe): flush every **5 s**;
**5 000** distinct pending keys in memory (excess dropped, and logged — never silently); **2 000** rows
per trust partition; retention on `DeliveryLogDays`.

## Open questions (non-blocking)

1. **Reply codes for throttling.** The 4xx/5xx defect above is recorded as a known issue, not fixed:
   `IMailboxFilter`'s `bool` cannot carry a code, so the response would have to travel some other way
   (a session property read by a custom command, or a fork of `MailCommand`). Consider whether it
   belongs upstream (`enhance-libraries-at-source.md`).
2. **Config mutations are not audited — repo-wide, and this page makes it pointed.** `[Allow IP]` creates
   a firewall-shaped allow rule from a list row in one click. Nothing is audited, which is *consistent*:
   `IpAccessRules.razor` and `AcceptedSenderDomains.razor` do not audit either, and `AdminAuditActions`
   has no config vocabulary at all (its 15 constants are identity/session/server-lifecycle). A
   confirmation dialog stands in for deliberateness today. Adding `config.*` audit actions is the real
   fix and needs a naming decision (`naming-discipline.md`), so it was not invented here.
3. **`AcceptedSenderDomainService` does not invalidate the runtime config cache; its callers must.**
   `IpAccessRuleService` does it itself, with a comment stating why ("so no caller can forget to and
   leave stale policy live"). The Rejections page follows the existing convention and invalidates
   caller-side, but the asymmetry is a trap for the next caller — moving it into the service would end
   the class of bug (`enhance-libraries-at-source.md`).

## Adjacent finding, not part of this feature

The integration suite passed only in whole-suite order. `IdentityDbContext.OnModelCreating` resolves
`IOptions<IdentityOptions>` from the application service provider and reads `Stores.SchemaVersion` to
decide whether the model contains the v3 passkey table, so a test host that does not register Identity
builds a **different model** than the migration snapshot and `MigrateAsync` throws
`PendingModelChangesWarning`. The suite only worked because `AdminBootstrapTests` (which registers
Identity) ran first alphabetically and warmed EF's model cache. The new tests register the single option
they need; `SmtpRelayEndToEndTests` still has the latent order dependency and would fail if run alone
(e.g. from an IDE).

## Prior art

- **pflogsumm** (Postfix) — the reference for a daily mail report with a reject breakdown by reason.
  Solved this shape decades ago; the daily-report half of this design is essentially pflogsumm.
- **Transactional ESPs** (SendGrid, Postmark, Mailgun) — "dropped" / "blocked" is a first-class
  activity event with a reason code, visible in the UI. No ESP expects a customer to read a log to
  find out why mail did not send. That is the bar.
- **Exchange Online message trace** — shows rejected messages, not only delivered ones.
