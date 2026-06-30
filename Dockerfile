# syntax=docker/dockerfile:1

# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only what restore needs first, so the restore layer is cached until
# dependencies actually change. Directory.Build.props is imported by every
# project, so it must be present before restore for a deterministic build.
COPY ["Directory.Build.props", "./"]
COPY ["src/CognitiveBudget.Web/CognitiveBudget.Web.csproj", "src/CognitiveBudget.Web/"]
RUN dotnet restore "src/CognitiveBudget.Web/CognitiveBudget.Web.csproj"

# Copy the rest and publish in one step (publish restores incrementally + builds).
COPY . .
RUN dotnet publish "src/CognitiveBudget.Web/CognitiveBudget.Web.csproj" \
        -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# curl is used by the container HEALTHCHECK below.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Non-root user, and a writable logs dir it owns (Serilog writes to /app/logs).
RUN addgroup --system appgroup \
    && adduser --system --ingroup appgroup appuser \
    && mkdir -p /app/logs \
    && chown -R appuser:appgroup /app

COPY --from=build --chown=appuser:appgroup /app/publish .

USER appuser

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "CognitiveBudget.Web.dll"]
