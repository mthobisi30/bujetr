# Deployment Guide

This document describes the steps to take your CognitiveBudget ASP.NET Core application from development to a live, production-ready service.

## 1. Prerequisites

- .NET 8 SDK (for building locally)
- Docker + Docker Compose (recommended for easy deployment)
- Access to a PostgreSQL 16-compatible database (e.g., Neon Postgres, your own server, or Docker container)
- (Optional) A domain name and TLS certificate (Let's Encrypt, etc.)

The repository already includes a `Dockerfile` and `docker-compose.yml` for local testing.

> **Security Update:** Database connection strings must be set via environment variables (see section 2 below). A `.env.example` file is provided as a template. Package versions are aligned with .NET 8.0 target framework.


## 2. Configuring the database

1. **Connection string**

   The app reads `ConnectionStrings:DefaultConnection` from configuration. In production set it via an environment variable:

   ```bash
   export ConnectionStrings__DefaultConnection="Host=<host>;Port=<port>;Database=<db>;Username=<user>;Password=<pass>;SSL Mode=Require;ChannelBinding=require"
   ```

   For a Neon database, the connection string follows this form (replace the
   placeholders with your own values — never commit real credentials):

   ```bash
   export ConnectionStrings__DefaultConnection='postgresql://<user>:<password>@<host>.neon.tech/<database>?sslmode=require&channel_binding=require'
   ```

2. **Apply migrations**

   The project includes EF Core migrations under `Data/Migrations`.
   Run the following once the database is accessible:

   ```bash
   cd src/CognitiveBudget.Web
   dotnet ef database update
   ```

   In Docker, the application will now run `db.Database.Migrate()` on startup regardless of the environment. This means you can simply bring the container up and it will create/upgrade the schema automatically. You can also continue to run the `dotnet ef database update` command manually if you prefer to control when migrations occur.


## 3. Building and running

### Locally (without containers)

```bash
cd src/CognitiveBudget.Web
# restore/build
dotnet build
# (ensure the connection string environment variable is set)
dotnet run
```

The site will listen on `http://localhost:5000` by default or `https://localhost:5001` if using HTTPS.

### Using Docker Compose

The repository provides `docker-compose.yml` that launches Postgres & the web service.

```bash
docker compose up --build
```

Use `--profile dev` to include pgAdmin for easy database exploration.


## 4. Production considerations

- **Environment:** set `ASPNETCORE_ENVIRONMENT=Production` and other sensitive settings via environment variables or a secrets store.
- **HTTPS:** The application will attempt HTTPS redirect in production unless `UseHttpsRedirection=false` is set. For deployments behind a reverse proxy (nginx, Traefik, etc.), set `UseHttpsRedirection=false` in your `.env` file and handle TLS termination at the proxy level.
- **Logging:** Serilog writes to console and file by default; mount a volume for `/app/logs` when running in containers.
- **Health checks:** the application exposes a `/health` endpoint that returns 200 when running. Wire this into your orchestrator or load balancer.
- **Background processing:** a hosted service (`TriggerBackgroundService`) automatically re-analyses user spending triggers once per day.
- **Scaling:** the app is stateless except for the database, so you can run multiple instances behind a load balancer.
- **Secrets:** never check in passwords. Use environment variables or a vault and configure the connection string accordingly.
- **Database upgrades:** when deploying a new version that changes the EF model, add an EF migration and apply it before/after rolling update as appropriate.


## 5. Additional operational steps

- **Create an admin user**: register via the UI, then assign any roles as needed by using `UserManager` in a small script or from the database.
- **Backups:** schedule regular backups of the PostgreSQL instance.
- **Monitoring:** collect logs with a centralized system (ELK, Datadog, etc.) and configure alerts on error rates.
- **Certificates & domains:** update `ASPNETCORE_URLS` or reverse proxy settings to listen on your desired host/port.


## 6. Future enhancements

- CSV/transaction import already implemented; extend to parse common bank formats.
- Add OAuth/third‑party banking sync (Plaid, etc.) for automatic transaction ingestion.
- Implement 2FA / email confirmation for Identity
- Containerize with Kubernetes using a Helm chart or other tooling.
- Add automated CI/CD pipeline that runs tests (`dotnet test`), builds images, and deploys to your host.

### CI/CD example

The `.github/workflows/ci.yml` file included with this repo is a starting point.
It fires on every push/PR to `main`, restores/builds the solution, runs the full
test suite (unit + integration), and — when changes land on `main` —
builds & pushes a Docker image to the container registry configured via
`DOCKER_HUB_*` secrets (you may substitute GHCR or another registry).  Adapt
this workflow to include additional steps such as security scanning, database
migration jobs, or deployment to a cloud provider.


---

Once the environment is configured and migrations applied, navigating to the host URL will bring up the login/registration page. From there you can begin adding transactions, creating commitment rules, and exploring the dashboard.
