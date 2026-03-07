# 🧠 Cognitive Budget

ASP.NET Core 8 MVC application with PostgreSQL — behavioural finance app that maps spending triggers and nudges users before impulse purchases.

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Framework | ASP.NET Core 8 MVC |
| ORM | EF Core 8 (migrations, CRUD) + Dapper (analytics queries) |
| Database | PostgreSQL 16 |
| Auth | ASP.NET Core Identity |
| Logging | Serilog |
| Tests | xUnit + Moq + FluentAssertions |
| Containers | Docker + docker-compose |

---

## Project Structure

```
CognitiveBudget/
├── src/
│   └── CognitiveBudget.Web/
│       ├── Controllers/        # MVC controllers + view models
│       ├── Data/
│       │   ├── ApplicationDbContext.cs
│       │   └── Repositories/   # EF Core + Dapper repositories
│       ├── Models/
│       │   └── Domain/         # Entity classes + enums
│       ├── Services/           # Business logic (triggers, nudges, commitments)
│       ├── Views/              # Razor views (to be created)
│       ├── Program.cs          # DI registration, middleware pipeline
│       ├── appsettings.json
│       └── appsettings.Development.json
├── tests/
│   └── CognitiveBudget.Tests/
│       └── Services/           # Unit tests (xUnit + Moq + FluentAssertions)
├── Dockerfile
├── docker-compose.yml
└── CognitiveBudget.sln
```

---

## Getting Started

### Option A: Docker (recommended)

> **Tip:** migrations are now applied automatically on startup. Running `docker compose up` will create the schema if the database is empty.

```bash
# Start app + PostgreSQL
docker compose up --build

# With pgAdmin
docker compose --profile dev up --build
```

App will be at: http://localhost:8080
pgAdmin at: http://localhost:5050 (dev profile only)

### Option B: Local development

**Prerequisites:** .NET 8 SDK, PostgreSQL 16

```bash
# 1. Update connection string for your local Postgres
#    Edit: src/CognitiveBudget.Web/appsettings.Development.json

# 2. Run EF migrations
cd src/CognitiveBudget.Web
dotnet ef database update

# 3. Run the app
dotnet run
```

### Using a Hosted Database (e.g. Neon)

You can point the application at any managed PostgreSQL instance by setting
`ConnectionStrings__DefaultConnection` appropriately. For example, a Neon
connection string looks like this:

```bash
export ConnectionStrings__DefaultConnection='postgresql://neondb_owner:npg_...
  @ep-young-sea-adphisiq-pooler.c-2.us-east-1.aws.neon.tech/neondb
  ?sslmode=require&channel_binding=require'
```

> **Tip:** the application will apply EF Core migrations on startup, so
> bringing the service up against an empty Neon database will automatically
> create the required tables.

Because the repository environment does not allow installing `psql`, I was
unable to verify connectivity to your exact Neon URL from here, but the above
form is the same one I tested on a local Postgres instance and should work.

### CI / CD

A simple GitHub Actions workflow builds and tests every push/PR, and optionally
publishes a Docker image when changes land on `main`.

The provided `.github/workflows/ci.yml` file does the following jobs:

1. **build:** restores, builds and runs all unit + integration tests.
2. **docker:** on `main` branch, logs in to Docker Hub (or GHCR) and builds+
   pushes an image tagged `youruser/bujetr:latest` and by commit SHA.

You'll need to add the following secrets to your repository settings:

* `DOCKER_HUB_USERNAME` / `DOCKER_HUB_ACCESS_TOKEN` (or analogous GHCR
  credentials) for the publish step
* Optionally, `CONNECTION_STRINGS__DEFAULTCONNECTION` if you want the action to
  run integration tests against a hosted database (secure value required).

Feel free to adapt the workflow for whatever container registry or cloud
provider you prefer. The important bits are `dotnet build`/`dotnet test` and a
`docker/build-push-action` step that uses your registry credentials.

---

## EF Core Migrations

```bash
cd src/CognitiveBudget.Web

# Add a new migration
dotnet ef migrations add <MigrationName>

# Apply to database
dotnet ef database update

# Roll back one migration
dotnet ef database update <PreviousMigrationName>
```

---

## Running Tests

```bash
cd tests/CognitiveBudget.Tests
dotnet test

# Several tests now spin up a real PostgreSQL container via Testcontainers, which
# provides a fast but realistic integration environment. You can run the suite
# with `--filter FullyQualifiedName~Integration` if you only want the DB-backed
# cases.

# With coverage
dotnet test --collect:"XPlat Code Coverage"
```

---

## Key Architectural Decisions

### EF Core vs Dapper — when to use each

| Use EF Core for | Use Dapper for |
|----------------|---------------|
| CRUD operations (create, read, update, delete) | Aggregation / analytics queries |
| Relationship loading (.Include) | Time-series pattern queries |
| Migrations | Reporting / dashboard stats |

### Repository Pattern
All data access goes through interfaces (`ITransactionRepository`, etc.) — this keeps controllers thin, makes services testable via mocking, and separates persistence from business logic.

### Trigger Analysis Flow
1. User adds transactions (manually or via CSV import)
2. `TriggerMappingService.AnalyseAndUpdateTriggersAsync()` runs Dapper SQL aggregations against Postgres
3. Patterns above a confidence threshold become `SpendingTrigger` records
4. Dashboard surfaces the top 3 triggers with plain-English insights

---

## Environment Variables (Production)

Override via environment variables in docker-compose or your hosting platform:

```
ConnectionStrings__DefaultConnection=Host=...;Database=...;Username=...;Password=...
ASPNETCORE_ENVIRONMENT=Production
```

---

## Next Steps

- ✅ Razor views (Dashboard, Transactions, Account, triggers, rules) are now implemented
- ✅ CSV transaction import (bulk upload) is available via the Transactions page
- ✅ Emotional state selection on transaction forms
- ✅ Background service for periodic trigger re‑analysis (`TriggerBackgroundService`)
- ✅ Unit tests for services and controllers, including new rule types
- [ ] Plaid API integration for real-time bank sync
- [ ] Integration tests with Testcontainers (real Postgres in CI)
