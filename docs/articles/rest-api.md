# REST API

`WinSmtpRelay.AdminApi` exposes a REST API (ASP.NET Core Minimal API) for management and monitoring. It is
the **out-of-process** reuse contract: drive a running relay from another process or language over HTTP.

## Authentication

Every endpoint requires authentication. For automation, create an API key in the admin UI and send it as:

```
X-Api-Key: <key>
```

or

```
Authorization: Bearer <key>
```

Keys are scoped to a **tenant** and a **role** (host admin / tenant admin / viewer), so a key only sees and
changes its own tenant's data. A key cannot create or re-scope a key with more power than its own. Failed key
logins are throttled per address: after 10 failures in 5 minutes the source gets 429 before any lookup.

## Requests and errors

- Request bodies are validated DTOs: identifiers and timestamps in a body are ignored, invalid input answers **400**,
  an unknown id **404**, a conflict (duplicate name, a resource still in use) **409**.
- Send connectors take `password`: omitted or `null` keeps the stored password, `""` clears it. Responses never
  contain a password or a DKIM private key.
- `limit` is capped at 500; a negative `offset` answers 400.
- `GET /api/rate-limits` and `PUT /api/rate-limits` are host-wide settings and need a host-admin key.
- Message bodies are returned only to a tenant-admin key of the message's tenant (host keys, also with
  `X-Relay-Tenant`, get 403); every read is written to the audit log.

## OpenAPI specification

The running admin host serves a machine-readable OpenAPI document to signed-in administrators at:

```
GET /openapi/v1.json
```

Point any OpenAPI tool at it — for example [Scalar](https://scalar.com/) or Swagger UI — to browse the
endpoints, models, and try requests. You can also import it into Postman/Insomnia or generate a typed
client.

> The admin host binds to `127.0.0.1` over HTTPS by default, so the spec is reachable at
> `https://localhost:8025/openapi/v1.json` on the machine running the service (adjust host/port to your
> deployment).
