# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/CognitiveBudget.Web/CognitiveBudget.Web.csproj", "src/CognitiveBudget.Web/"]
RUN dotnet restore "src/CognitiveBudget.Web/CognitiveBudget.Web.csproj"

COPY . .
WORKDIR "/src/src/CognitiveBudget.Web"
RUN dotnet build "CognitiveBudget.Web.csproj" -c Release -o /app/build

# ── Publish stage ─────────────────────────────────────────────────────────────
FROM build AS publish
RUN dotnet publish "CognitiveBudget.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Create non-root user for security
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=publish /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080
ENTRYPOINT ["dotnet", "CognitiveBudget.Web.dll"]
