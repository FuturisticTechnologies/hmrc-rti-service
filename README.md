# hmrc-rti-service

.NET 10 microservice that files **HMRC Real Time Information** submissions — FPS
and EPS — for committed pay runs. Covers SOW milestones **M6** (FPS) and **M7**
(EPS, error handling, reconciliation).

It does **not** recalculate payroll. It reads a committed pay run from the Python
payroll API and reports those figures verbatim; if RTI disagreed with the ledger,
the ledger would stop being the record of what was filed.

---

## How it fits the platform

| Concern | How it matches the existing services |
|---|---|
| Auth | Validates the **same HS256 token** the payroll API issues. Shared `APP_SECRET_KEY`, no separate login. |
| Tenancy | `X-Tenant-ID` header, then the `tenant_id` claim, then the seeded default — same precedence as `app/core/rbac.py`. |
| Isolation | Postgres RLS in the `rti` schema, using the same `app.tenant_id` / `app.is_superuser` session variables. |
| Permissions | `rti:read` / `rti:submit`, mapped in config onto `payroll:read` / `payroll:run`, so **no change to the Python permission catalog is required**. |
| Errors | The `{"detail": "..."}` envelope FastAPI produces. |
| Idempotency | `Idempotency-Key` with identical semantics to `app/core/idempotency.py`. |
| Migrations | Ordered `.sql` files applied on boot, mirroring `alembic upgrade head`. |
| Logging | Structured JSON on stdout. |

Tokens are issued once, at `POST /api/v1/auth/login` on the payroll API, and
work here unchanged.

## API

All routes are under `/api/v1`. OpenAPI document at `/openapi.json`.

| Method | Path | Policy |
|---|---|---|
| `GET` | `/api/v1/health`, `/health/ready`, `/meta` | anonymous |
| `POST` | `/api/v1/rti/fps` | `rti:submit` |
| `POST` | `/api/v1/rti/eps` | `rti:submit` |
| `GET` | `/api/v1/rti/submissions` | `rti:read` |
| `GET` | `/api/v1/rti/submissions/{id}` | `rti:read` |
| `GET` | `/api/v1/rti/submissions/{id}/payload` | `rti:read` |
| `GET` | `/api/v1/rti/submissions/{id}/events` | `rti:read` |
| `POST` | `/api/v1/rti/submissions/{id}/transmit` | `rti:submit` |
| `POST` | `/api/v1/rti/submissions/{id}/retry` | `rti:submit` |

## Submission lifecycle

```
Draft ──> Validated ──> Queued ──> Transmitting ──> Accepted   (terminal)
  │           │            │             ├────────> Rejected ──┐
  └──────────>└───────────>└─────────────┴────────> Failed ────┴──> Queued
```

`Accepted` is terminal by design: an accepted filing is amended by sending a
**further FPS**, never by mutating the one HMRC already holds. Every transition
goes through a single `Transition()` method that refuses illegal moves and
appends an audit event in the same step — there is no code path that assigns
`Status` directly, which is what makes the event log trustworthy.

Rejections and transport failures are treated differently. A 4xx business
rejection will fail identically if replayed unchanged, so no automatic retry is
scheduled; 5xx, 408 and 429 get exponential backoff (1, 2, 4, 8, 16 minutes) up
to five attempts.

## Live-submission gating

SOW gating rule 3 is enforced in code, not by convention. A live HMRC host is
refused unless **both** hold:

- `Hmrc:AllowLiveSubmission` is `true`, and
- `ASPNETCORE_ENVIRONMENT` is `Production`

Otherwise the request fails loudly rather than silently downgrading to sandbox —
a caller that believes it filed a return but did not is the worst outcome here.
`GET /api/v1/meta` reports the effective posture so an operator can see it.

With no HMRC credentials configured, the client returns a **simulated
acceptance** (and says so in the logs and the audit trail), so the full
lifecycle is exercisable offline.

## Running it

The `dotnet` CLI on this machine is at `C:\Program Files\dotnet\dotnet.exe` and
is not on `PATH`.

```bash
# Tests need no database
& "C:\Program Files\dotnet\dotnet.exe" test tests/Hmrc.Rti.Tests

# Running needs Postgres, so use the whole-platform stack from d:\FT
docker compose -f ../docker-compose.platform.yml up --build
```

Service listens on **:8081** in the platform stack.

## Configuration

Environment variables use the .NET double-underscore convention.

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings__Postgres` | localhost | Also accepts a `postgresql://` URL, including SQLAlchemy `+asyncpg` forms |
| `PlatformAuth__SecretKey` | — | **Must equal the payroll API's `APP_SECRET_KEY`** |
| `PayrollApi__BaseUrl` | `http://localhost:8000` | |
| `Hmrc__BaseUrl` | HMRC sandbox | |
| `Hmrc__ClientId` / `__ClientSecret` | empty | Empty means simulate |
| `Hmrc__AllowLiveSubmission` | `false` | See gating above |

## Layout

```
src/Hmrc.Rti.Api/
  Platform/       auth, request pipeline, migrator, idempotency  (shared, verbatim, with integration-hub)
  Domain/         submission entities and the state machine
  Infrastructure/ EF Core context, HMRC client, payroll API client
  Application/    envelope builder + validation, submission orchestration
  Endpoints/      minimal-API route definitions
migrations/       ordered SQL, applied on boot
tests/            36 unit tests, no database required
```

The `Platform/` folder is byte-identical to the one in `integration-hub`,
deliberately: two self-contained services that can be built and deployed
independently. If a third service appears, extract it to a shared package.
