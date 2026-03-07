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

- [ ] Add Razor views (Dashboard, Transactions, Account pages)
- [ ] CSV transaction import (bulk upload)
- [ ] Plaid API integration for real-time bank sync
- [ ] Emotional check-in UI (post-transaction mood prompt)
- [ ] Background service for periodic trigger re-analysis
- [ ] Integration tests with Testcontainers (real Postgres in CI)
