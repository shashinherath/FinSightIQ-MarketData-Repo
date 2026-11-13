# ==============================================================================
# FinSightIQ Market Data Service — Multi-stage Containerfile (.NET 8)
# ==============================================================================

# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project definitions first for optimal layer caching
COPY ["global.json", "./"]
COPY [".editorconfig", "./"]
COPY ["nuget.config", "./"]
COPY ["packages/", "./packages/"]
COPY ["Application/FinSightIQ.MarketDataService.Core/FinSightIQ.MarketDataService.Core.csproj", "Application/FinSightIQ.MarketDataService.Core/"]
COPY ["Application/FinSightIQ.MarketDataService.Infrastructure/FinSightIQ.MarketDataService.Infrastructure.csproj", "Application/FinSightIQ.MarketDataService.Infrastructure/"]
COPY ["Application/FinSightIQ.MarketDataService.Api/FinSightIQ.MarketDataService.Api.csproj", "Application/FinSightIQ.MarketDataService.Api/"]

# Restore packages with optional BuildKit secret for GitHub Packages access
RUN --mount=type=secret,id=nuget_token,required=false \
    if [ -f /run/secrets/nuget_token ]; then \
        export NUGET_TOKEN=$(cat /run/secrets/nuget_token); \
    fi && \
    export NUGET_TOKEN="${NUGET_TOKEN:-ghp_dev_token_placeholder}" && \
    dotnet restore "Application/FinSightIQ.MarketDataService.Api/FinSightIQ.MarketDataService.Api.csproj"

# Copy source code and build
COPY ["Application/FinSightIQ.MarketDataService.Core/", "Application/FinSightIQ.MarketDataService.Core/"]
COPY ["Application/FinSightIQ.MarketDataService.Infrastructure/", "Application/FinSightIQ.MarketDataService.Infrastructure/"]
COPY ["Application/FinSightIQ.MarketDataService.Api/", "Application/FinSightIQ.MarketDataService.Api/"]
WORKDIR "/src/Application/FinSightIQ.MarketDataService.Api"

RUN dotnet build "FinSightIQ.MarketDataService.Api.csproj" -c Release --no-restore
RUN dotnet publish "FinSightIQ.MarketDataService.Api.csproj" -c Release --no-build -o /app/publish /p:UseAppHost=false

# Stage 2: Production Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Run as built-in non-root app user
USER $APP_UID

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5102 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5102
EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=3s --retries=3 --start-period=5s \
    CMD curl -f http://localhost:5102/health/live || curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "FinSightIQ.MarketDataService.Api.dll"]

