# Security Policy

## Status

WIN-SMTP-RELAY is pre-1.0 and under active development. Run it only where you can apply updates
promptly, and review the **Responsible operation** guidance below before exposing it to a network.

## Reporting a vulnerability

Please report security vulnerabilities **privately** — do not open a public GitHub issue for them.

- Use GitHub's private vulnerability reporting: the repository **Security** tab → **Report a vulnerability**.

Include affected version/commit, a description, reproduction steps, and impact. We aim to acknowledge
within a few business days. Please give us a reasonable window to fix and release before any public
disclosure.

## Responsible operation

WIN-SMTP-RELAY is a mail transfer agent: misconfiguration or misuse can turn any MTA into an abuse
vector and get your sending IP blocklisted. Operate it responsibly.

### Do not run an open relay

By design, **relaying to an external (non-hosted) recipient always requires SMTP authentication or an
explicit allow-IP rule**. This protection is enforced in code and **cannot be disabled by
configuration** — an empty configuration, or a single `0.0.0.0/0` / `::/0` allow rule, will *not* relay
to the outside world. Even so:

- Prefer **SMTP authentication** for clients that submit mail.
- If you authorize by IP, add **specific** allow-IP rules (the narrowest range that works) — never rely
  on a broad/"any" rule for relaying.
- Require AUTH on internet-facing submission endpoints (587/465); keep port 25 for inbound/MX only.

### Send only solicited mail; handle bounces and complaints

- Send only mail your recipients have asked for. Unsolicited mail gets your IP and domain blocklisted.
- Honor unsubscribe requests; do not strip `List-Unsubscribe` headers added by senders.
- Stop sending to addresses that hard-bounce or complain. WIN-SMTP-RELAY maintains a per-tenant
  suppression list (auto-populated from permanent bounces and complaints) and refuses further delivery
  to suppressed addresses — do not work around it.

### Protect deliverability and your IP reputation

- Publish **SPF, DKIM and DMARC** for every sending domain, set valid **reverse DNS (PTR)** that matches
  your public hostname (FCrDNS), and use a stable IP — not a dynamic/residential one.
- For production volume, deliver through a **reputable smart host** (e.g. Azure Communication Services
  Email, Amazon SES, Brevo, Mailjet) rather than direct-to-MX from your own IP, so a managed,
  reputation-warmed IP pool carries your mail. (On some clouds, e.g. Azure, outbound port 25 is blocked,
  making a smart host mandatory.)
- Watch the **Health** page (it checks Spamhaus ZEN / SpamCop and your SPF/DKIM/DMARC/PTR) and act on
  any listing immediately — find and stop the cause, then request delisting.

### Protect the admin plane

- Serve the admin UI over **HTTPS** with a real certificate; keep it on loopback or behind a reverse
  proxy / restricted network — never expose it unauthenticated.
- Use strong admin passwords and change the seeded one-time password on first sign-in. Scope API keys to
  the least privilege (tenant + role) needed.
