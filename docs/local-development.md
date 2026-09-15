# Local development — HMRC RTI service

Owner: Platform Engineer (.NET).

This service is .NET in a platform that is otherwise Python and Angular, so the
things that trip people up are mostly about how it fits in rather than about
the code. Those are the things written down here.

## 1. Prerequisites

| Requirement | Version | Note |
|---|---|---|
| .NET SDK | **10.0.400+** | Not 8. See §2 |
| Docker | Any current | Needed for Postgres |
| Postgres | 16 | Shared with the payroll API |

```bash
dotnet --version    # must report 10.x
```

## 2. The .NET 10 requirement

The projects target `net10.0`. A `net8.0` build **compiles** here and then fails
at startup with a missing-framework error, because the build environment has SDK
10.0.400 with only the ASP.NET Core 10.0.x runtime installed.

This is worth stating plainly because the failure is confusing: the compiler is
happy, and the problem only appears at run time. If `dotnet run` reports a
missing framework, check the SDK before checking the code.

## 3. Running it

The service applies its SQL migrations on boot, so it needs a database to start
at all. There is no offline mode — that is deliberate, since a service that
silently starts without its schema is a worse failure than one that refuses to.

```bash
# From the platform root, brings up Postgres plus both .NET services.
docker compose -f docker-compose.platform.yml up

# Or, against a Postgres you already have running:
export DATABASE_URL="postgresql://payroll:payroll_dev_pwd@localhost:5432/payroll"
dotnet run --project src/Hmrc.Rti.Api      # http://localhost:8081, Development (launch profile)
```

`DATABASE_URL` accepts either a native Npgsql connection string or the
SQLAlchemy-style URL the Python service uses, including the `+asyncpg` and
`+psycopg2` driver suffixes. One `DATABASE_URL` works for the whole platform,
which is the point.

`ConnectionStrings__Postgres`, when set, takes precedence over `DATABASE_URL`
(it is what Azure sets); with neither, the service uses
`localhost:5432/payroll`.

## 4. Ports

| Service | Port |
|---|---|
| Payroll API (Python) | 8000 |
| HMRC RTI service | 8081 |
| Integration hub | 8082 |
| Frontend | 4200 |

## 5. Authentication — there is no second login

The service validates the **same HS256 token the Python API issues**. It does
not have its own login endpoint and will not be given one.

```bash
# Get a token from the payroll API...
TOKEN=$(curl -s -X POST http://localhost:8000/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@payrollfuturistic.co.uk","password":"Admin12345!"}' \
  | python -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')

# ...and use it here.
curl http://localhost:8081/api/v1/meta -H "Authorization: Bearer $TOKEN"
```

Both services must share `APP_SECRET_KEY`. If they do not, every request here
returns 401 and the reason is not obvious from the error — that is the single
most common local setup failure.

## 6. Tests

```bash
dotnet test tests/Hmrc.Rti.Tests/Hmrc.Rti.Tests.csproj
```

There is no solution file, so `dotnet test` with no argument fails with MSB1003.
Point it at the project.

The unit tests need no database. They cover the envelope builders, the
submission state machine, late-reporting rules and the rejection catalog — all
pure logic. Anything touching Postgres or HMRC is out of scope for them, which
means a green run proves less than it looks like it proves. See §8.

## 7. HMRC transmission is gated

Live submission requires **all** of:

- `Hmrc:AllowLiveSubmission` = true
- The environment is Production
- Client credentials configured

Anything less and the service simulates. Check what a running instance actually
believes:

```bash
curl http://localhost:8081/api/v1/meta -H "Authorization: Bearer $TOKEN"
# -> hmrc.liveSubmissionPermitted
```

That field reflects the gating rule at run time rather than restating the
config, so it is the honest answer to "could this thing file for real right now".

## 8. What is not yet verified

Everything in this service is compiler- and unit-test verified. As of the last
update it had **not** been run end to end against a live Postgres and the HMRC
sandbox. The migrations, the RLS policies and the whole HTTP surface are
unexercised until that happens.

"Builds clean and the tests pass" is not "works". Tracked as M6-12.
