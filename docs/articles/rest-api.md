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
changes its own tenant's data.

## OpenAPI specification

The running admin host serves a machine-readable OpenAPI document at:

```
GET /openapi/v1.json
```

Point any OpenAPI tool at it — for example [Scalar](https://scalar.com/) or Swagger UI — to browse the
endpoints, models, and try requests. You can also import it into Postman/Insomnia or generate a typed
client.

> The admin host binds to `127.0.0.1` over HTTPS by default, so the spec is reachable at
> `https://localhost:8025/openapi/v1.json` on the machine running the service (adjust host/port to your
> deployment).
