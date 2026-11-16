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

All routes are under `/api/v1`. Swagger UI at `/swagger`, OpenAPI document at
`/openapi.json`.

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

## Statutory Maternity Pay (SNOV-141)

The payroll API sends no maternity data, so the caller supplies it. An FPS
request may carry a `maternity_pay` list; each entry (matched to a pay run line
by `employee_number`) is assessed, scheduled and attached to the FPS payload as
`employees[].statutoryMaternityPay`. The code is in `Application/Smp/` and is
pure: no database, no HTTP.

| Step | What it does |
|---|---|
| Eligibility | 26 weeks' continuous employment into the qualifying week (15th before the due week), average weekly earnings at or above the lower earnings limit, pregnancy status at the 11th week before the due week, SMP start no earlier than that week, 28 days' notice, MATB1 |
| Schedule | 39 weeks from the SMP start date in 7-day blocks. Weeks 1-6 at 90% of average weekly earnings; weeks 7-39 at the lower of the standard weekly rate and 90% |
| Contractual pay | The contractual amount includes SMP and never leaves the employee with less. The excess is a top-up, reported separately and never recoverable |
| Recovery | `SmpRecovery`: 92% for standard employers; 100% plus 3% NIC compensation for small employers. Report on the EPS with `smp_recovered` and `smp_nic_compensation` |

**Evidence.** The payload carries the weeks that overlap the pay period, each
with its band and amounts, and a `rateChange` block giving the week (7), its
start date, the amounts before and after, and whether it falls `before_period`,
`in_period` or `after_period`. The same fact is written to the submission's
audit trail as an `smp_assessed` event.

**Refusals.** An ineligible employee, an employee not on the pay run, a pay
period the SMP does not overlap, or a tax year with no configured rates all
fail local validation: 422, with the submission kept as `Failed` and the
evidence in its payload. Maternity pay is never dropped or guessed.

**Rates.** `SmpRates.cs` holds the standard rate, lower earnings limit and the
date each rate takes effect, per tax year. A year that is not in the table
raises instead of falling back to the nearest one. Add a row each April.
Average weekly earnings is an input: payroll owns the pay history.

Not handled: premature births (which move the qualifying week) and an SMP start
later than the requested leave start.

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

## Running it locally

Everything a new developer needs to build, run and test the service on their
own machine. Commands are given for PowerShell and Git Bash on Windows (Git
Bash commands work unchanged on macOS and Linux), run from the repository root
unless a step says otherwise.

### 1. Prerequisites

| Tool | Version | What it is for |
|---|---|---|
| .NET SDK | **10.0.x**. The projects target `net10.0`, the `Dockerfile` builds on `mcr.microsoft.com/dotnet/sdk:10.0` and CI installs `10.0.x`. There is no `global.json`, so any 10.0 SDK works. | Build, run and test. `dotnet --version` must report `10.x` |
| PostgreSQL | **16**. CI and the payroll API's compose file both use `postgres:16-alpine` | The service's database. **Required**: migrations are applied on boot, so the service will not start without it |
| Docker Desktop, with Compose v2 | Any current release | Runs Postgres (and optionally this service and the payroll API) in containers. Optional if you already have a Postgres 16 you can reach |
| Git | Any current release | Cloning |
| Payroll API ([`national-insurance`](https://github.com/FuturisticTechnologies/national-insurance)) | Its `development` branch, run with its own `docker compose` (or natively with Python 3.12) | Issues the tokens this service accepts, and serves the pay runs an FPS is built from. Optional for health checks and Swagger; required for any `/api/v1/rti/*` call |

### 2. Get the code

```bash
git clone https://github.com/FuturisticTechnologies/hmrc-rti-service.git
cd hmrc-rti-service
git checkout development
```

Day-to-day work happens on `development`; branch from it. `main` is what is
deployed: a push to `main` builds the image and rolls it out to the dev
Container App (`.github/workflows/deploy.yml`).

For the whole platform, clone the payroll API **next to** this repository
(step 4, option B uses it):

```bash
cd ..
git clone https://github.com/FuturisticTechnologies/national-insurance.git
```

### 3. Configure

The committed `src/Hmrc.Rti.Api/appsettings.json` and `Program.cs` already hold
working local defaults, so a first run needs no configuration at all:

| Setting | Local default | Why it works |
|---|---|---|
| Database | `Host=localhost;Port=5432;Database=payroll;Username=payroll;Password=payroll_dev_pwd` (built into `Program.cs`, used when neither `ConnectionStrings:Postgres` nor `DATABASE_URL` is set) | Matches the Postgres in step 4 |
| `PlatformAuth:SecretKey` | `change-me-in-production-this-must-be-at-least-32-chars` | Equals the payroll API's default `APP_SECRET_KEY` |
| `PayrollApi:BaseUrl` | `http://localhost:8000` | Where the payroll API listens locally |
| `Hmrc:ClientId` / `Hmrc:ClientSecret` | empty | Empty means HMRC is **simulated**: a transmit returns a simulated acceptance (`SIM-…`) and says so in the log and the audit trail |

Override any of them with environment variables, using a double underscore for
nesting (`ConnectionStrings__Postgres`, `PlatformAuth__SecretKey`,
`PayrollApi__BaseUrl`, `Hmrc__ClientId`, `Hmrc__ClientSecret`, …), or with a
`src/Hmrc.Rti.Api/appsettings.Development.json`, which is gitignored and only
loaded when `ASPNETCORE_ENVIRONMENT=Development`. Every key is listed under
[Configuration](#configuration).

Easy to get wrong:

- `PlatformAuth:SecretKey` must be **exactly** the payroll API's
  `APP_SECRET_KEY`, and at least 32 characters. Change one, change both.
- The database is taken from `ConnectionStrings__Postgres` if anything sets it
  (environment, `appsettings.Development.json`, command line), otherwise from
  `DATABASE_URL`, otherwise the local default above. Either variable accepts a
  native connection string or a `postgresql://` URL, including the
  `postgresql+asyncpg://` form the payroll API uses. Set both and
  `ConnectionStrings__Postgres` wins — it is what Azure sets.
- `dotnet run` does not read `.env` files. `.env.example` is only a list of the
  same keys.
- Leave `Hmrc__AllowLiveSubmission` at `false` locally — see
  [Live-submission gating](#live-submission-gating).

### 4. Start Postgres

Pick **one** — both use port 5432.

**A. Postgres on its own**, matching the default connection string:

```bash
docker run -d --name ftpay-postgres -p 5432:5432 -e POSTGRES_USER=payroll -e POSTGRES_PASSWORD=payroll_dev_pwd -e POSTGRES_DB=payroll postgres:16-alpine
docker exec ftpay-postgres pg_isready -U payroll -d payroll
```

**B. The payroll API's stack** — Postgres plus the payroll API on
http://localhost:8000 (and its frontend on 4200). From the `national-insurance`
clone:

```bash
cd ../national-insurance
cp .env.example .env
docker compose up --build -d
```

That Postgres (container `payroll-postgres`) has the same database, user and
password, so this service's defaults point at it unchanged. The platform shares
one database; each service keeps to its own schema.

There are no migrations to run by hand. On start-up the service applies every
`migrations/*.sql` file it has not applied before, in filename order, and
records each in `rti.schema_migrations`. The first boot creates the `rti`
schema and logs `migrations.applied 0001_rti_schema`.

### 5. Run the service

**With `dotnet run`** — listens on http://localhost:8081, the port the platform
uses for this service, as `Development`. Both come from the `http` profile in
`src/Hmrc.Rti.Api/Properties/launchSettings.json`, so no variables are needed
(PowerShell or Git Bash):

```bash
dotnet run --project src/Hmrc.Rti.Api
```

To listen elsewhere, pass `--urls` after `--`, which overrides the profile:
`dotnet run --project src/Hmrc.Rti.Api -- --urls http://localhost:9081`.
The profile is only read by `dotnet run` and IDEs; it is not published, so the
Docker image and Azure never see it. `Ctrl+C` stops it.

**With Docker** — builds the repository's `Dockerfile`. The container listens on
8080, published here as 8081. There is no compose file in this repository.

PowerShell:

```powershell
docker build -t hmrc-rti-service .
docker run --rm --name hmrc-rti -p 8081:8080 `
  -e "ConnectionStrings__Postgres=Host=host.docker.internal;Port=5432;Database=payroll;Username=payroll;Password=payroll_dev_pwd" `
  -e "PayrollApi__BaseUrl=http://host.docker.internal:8000" `
  hmrc-rti-service
```

Git Bash:

```bash
docker build -t hmrc-rti-service .
docker run --rm --name hmrc-rti -p 8081:8080 \
  -e "ConnectionStrings__Postgres=Host=host.docker.internal;Port=5432;Database=payroll;Username=payroll;Password=payroll_dev_pwd" \
  -e "PayrollApi__BaseUrl=http://host.docker.internal:8000" \
  hmrc-rti-service
```

Inside a container `localhost` is the container itself, hence
`host.docker.internal` (Docker Desktop's name for your machine). The image
already sets `ASPNETCORE_ENVIRONMENT=Development` and
`ASPNETCORE_HTTP_PORTS=8080` and ships `appsettings.json`, so the secret
default still matches the payroll API's; add `-e PlatformAuth__SecretKey=...`
if yours differs.

### 6. Verify

| URL | Expect |
|---|---|
| http://localhost:8081/api/v1/health | `200` `{"status":"ok","service":"hmrc-rti-service",…,"environment":"development"}`. Liveness; never touches the database |
| http://localhost:8081/api/v1/health/ready | `200` `{"status":"ready","database":"ok"}`, or `503` when Postgres is unreachable |
| http://localhost:8081/api/v1/meta | HMRC posture — locally `credentials_configured: false`, `live_submission_permitted: false` |
| http://localhost:8081/swagger | Swagger UI (redirects to `/swagger/index.html`) |
| http://localhost:8081/openapi.json | The OpenAPI document |

To call a secured endpoint, log in to the payroll API (running on port 8000 —
step 4, option B) and present its `access_token`. The payroll API seeds a
development admin, whose token carries every permission this service checks.

PowerShell:

```powershell
$login = Invoke-RestMethod -Method Post -Uri http://localhost:8000/api/v1/auth/login `
  -ContentType "application/json" `
  -Body '{"email":"admin@payrollfuturistic.co.uk","password":"Admin12345!"}'
Invoke-RestMethod -Uri http://localhost:8081/api/v1/rti/submissions `
  -Headers @{ Authorization = "Bearer $($login.access_token)" }
```

Git Bash:

```bash
TOKEN=$(curl -s -X POST http://localhost:8000/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@payrollfuturistic.co.uk","password":"Admin12345!"}' \
  | sed -E 's/.*"access_token":"([^"]+)".*/\1/')
curl -s http://localhost:8081/api/v1/rti/submissions -H "Authorization: Bearer $TOKEN"
```

A fresh database returns an empty list (`[]`; PowerShell prints nothing). In
Swagger, click **Authorize** and paste the `access_token` value. Filing goes one
step further: `POST /api/v1/rti/fps` forwards your token to
`GET /api/v1/pay-runs/{id}` on the payroll API, so it needs a committed pay run
there.

### 7. Tests

```bash
dotnet test tests/Hmrc.Rti.Tests/Hmrc.Rti.Tests.csproj
```

No database or payroll API needed; expect `Passed!  - Failed: 0, Passed: 259`.
There is no solution file, so always name the project — a bare `dotnet test`
fails with MSB1003.

CI restores the test project alone — `dotnet restore` accepts one project
(two fail with **MSB1008**), and the test project pulls in the API through its
project reference — then builds and tests in Release. To run the CI sequence
locally:

```bash
dotnet restore tests/Hmrc.Rti.Tests/Hmrc.Rti.Tests.csproj
dotnet build tests/Hmrc.Rti.Tests/Hmrc.Rti.Tests.csproj --configuration Release --no-restore
dotnet test tests/Hmrc.Rti.Tests/Hmrc.Rti.Tests.csproj --configuration Release --no-build
```

### 8. Stop and reset

- **Service** (`dotnet run` or `docker run`): `Ctrl+C`. The container was
  started with `--rm`, so nothing is left behind.
- **Postgres, option A**: `docker stop ftpay-postgres`, and
  `docker start ftpay-postgres` to bring it back. To wipe it:
  `docker rm -f -v ftpay-postgres`, then repeat step 4.
- **Payroll API stack, option B**: in `national-insurance`, `docker compose down`;
  `docker compose down -v` also deletes its database volume.
- **Only this service's data**: drop its schema; the next boot re-creates it.
  With option B, use the container name `payroll-postgres`.

  ```bash
  docker exec ftpay-postgres psql -U payroll -d payroll -c "DROP SCHEMA rti CASCADE;"
  ```

### 9. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Every secured call returns `401 {"detail":"Authentication required"}`, although login worked | The token was not signed with this service's `PlatformAuth:SecretKey`; it must equal the payroll API's `APP_SECRET_KEY`. Also check the token's age — access tokens expire after 30 minutes (the payroll API's `JWT_ACCESS_TOKEN_EXPIRE_MINUTES`), so log in again — and that you sent `access_token`, not `refresh_token`, which is refused here |
| Start-up fails with `PlatformAuth:SecretKey must be set and at least 32 characters` | The secret is empty or shorter than 32 characters |
| Start-up fails with `Npgsql.NpgsqlException: Failed to connect to 127.0.0.1:5432` | Postgres is not running, or not at that host and port. Migrations run before the service starts listening, so nothing answers on 8081 at all. If Postgres goes away later, `/api/v1/health/ready` returns `503 {"detail":"Database is not reachable"}` while `/api/v1/health` stays `200` |
| A `DATABASE_URL` you set has no effect | `ConnectionStrings:Postgres` is also set — in the environment or `appsettings.Development.json` — and takes precedence (step 3). Remove it, or put the URL there instead |
| `Failed to bind to address http://127.0.0.1:8081: address already in use`, or the service is on `http://localhost:5000` and `/api/v1/meta` says `production` | Another process holds the port: choose another with `-- --urls` (step 5). Port 5000 and `production` mean the launch profile was not used (`--no-launch-profile`, or the built DLL was started directly); run `dotnet run --project src/Hmrc.Rti.Api`. If a native Postgres already has 5432, publish the container as `-p 5433:5432` and put `Port=5433` in `ConnectionStrings__Postgres` |
| `POST /api/v1/rti/fps` returns `502 The payroll API is unreachable.`, `401 The payroll API rejected the forwarded credentials.` or `404 Pay run … was not found.` | 502: `PayrollApi:BaseUrl` is wrong or the payroll API is down (from a container it must be `host.docker.internal`, not `localhost`). 401: the token was not issued by that payroll API, or its user is not in that API's database. 404: no such pay run for the token's tenant |

Background on the .NET 10 requirement, HMRC gating and what the unit tests do
not cover is in [`docs/local-development.md`](docs/local-development.md).

## Configuration

Environment variables use the .NET double-underscore convention.

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings__Postgres` | unset | Also accepts a `postgresql://` URL, including SQLAlchemy `+asyncpg` forms. Wins over `DATABASE_URL`; Azure sets this one |
| `DATABASE_URL` | unset | Read when `ConnectionStrings__Postgres` is not set; same formats. With neither, the service uses localhost:5432, database `payroll` |
| `PlatformAuth__SecretKey` | `change-me-in-production-this-must-be-at-least-32-chars` (local only) | **Must equal the payroll API's `APP_SECRET_KEY`**; at least 32 characters |
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
tests/            259 unit tests, no database required
```

The test count above is collected cases, not test methods — `[Theory]` rows
count individually, which is what the runner reports. Reproduce it with:

```bash
dotnet test tests/Hmrc.Rti.Tests --list-tests
```

Quote that number rather than a remembered one. This figure was documented as
36, then 89, then 104, and was wrong each time within a sprint of being written — not
because anyone estimated, but because later tickets added tests and nobody came
back to the README.

The `Platform/` folder is byte-identical to the one in `integration-hub`,
deliberately: two self-contained services that can be built and deployed
independently. If a third service appears, extract it to a shared package.
