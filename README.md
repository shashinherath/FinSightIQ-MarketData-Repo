# FinSightIQ Market Data Service (`finsightiq-market-data-service`)

The OHLCV time-series ingestion, upstream provider failover, partitioned PostgreSQL storage, Redis quote caching, RabbitMQ price-event publishing, and market data REST API microservice for the **FinSightIQ** AI-driven stock prediction and portfolio analytics platform.

Built on **ASP.NET Core (.NET 8)**, **Clean Architecture**, **PostgreSQL 15+**, **Dapper + Npgsql**, **Flyway**, **Redis 7+**, and **RabbitMQ 3.12+**.

---

## 1. Architecture & Service Responsibilities

The Market Data Service owns the `marketdb` database (`symbols`, `stock_prices`, `provider_health`) and enforces Clean Architecture dependency rules (`Api` → `Infrastructure` → `Core`, where `Core` has zero infrastructure dependencies):

- **Multi-Provider Ingestion & Failover (`FR-MKT-01`, `FR-MKT-02`, `FR-MKT-12`):**
  - Primary provider: **Alpha Vantage** (`AlphaVantageProvider`).
  - Fallback provider: **Yahoo Finance** (`YahooFinanceProvider`).
  - Configured with `Microsoft.Extensions.Http.Resilience` (3 retries with exponential backoff and jitter; circuit breaker opens after 5 consecutive failures / 50% failure ratio for a 30-second break window).
  - `MarketDataProviderOrchestrator` persists circuit state (`Closed`, `Open`, `HalfOpen`), consecutive failures, and timestamps in `provider_health`.
- **NYSE Exchange-Aware Polling (`FR-MKT-03`, `FR-MKT-04`):**
  - `PricePollingService` (`BackgroundService`) runs on a configurable 60-second cadence without blocking application startup.
  - `NyseMarketHoursGuard` evaluates `America/New_York` Eastern Time across US DST transitions, polling strictly during regular trading hours (`09:30–16:00 ET`, Monday–Friday) and skipping weekends and NYSE holidays (New Year's Day, MLK Jr. Day, Presidents' Day, Good Friday, Memorial Day, Juneteenth, Independence Day, Labor Day, Thanksgiving, Christmas).
- **Partitioned Time-Series Storage & Bulk Ingestion (`FR-MKT-05`):**
  - `stock_prices` is range-partitioned monthly by `recorded_at` (`stock_prices_YYYY_MM`).
  - Upcoming monthly partitions are automatically provisioned 3 months ahead via `sp_EnsureStockPricePartition`.
  - Enforces OHLCV invariants (`OhlcvBarValidator`) and database `CHECK` constraints (`ck_prices_hl`, `ck_prices_band`, `ck_prices_vol`) plus idempotency (`ux_prices_symbol_interval_time` on `(symbol, interval, recorded_at)`).
  - Uses `NpgsqlBinaryImporter` (`COPY ... FROM STDIN (FORMAT BINARY)`) into a transactional staging table followed by an idempotent `INSERT ... ON CONFLICT DO UPDATE`.
- **Redis Cache-Aside (`FR-MKT-06`):**
  - Caches live quotes under `finsightiq:market:quote:{SYMBOL}` with a **60-second TTL** and symbol metadata under `finsightiq:market:symbols:{SYMBOL}` with a **24-hour TTL** via `IDistributedCache`.
  - Degrades gracefully to PostgreSQL when Redis is unavailable without failing requests.
- **RabbitMQ Event Publishing (`FR-MKT-10`):**
  - Publishes `FinSightIQ.Contracts.Events.PriceUpdated` messages to the durable topic exchange `market.price.updated` (routing key `market.price.updated`) with `X-Correlation-Id` header propagation.

---

## 2. Repository Structure

```text
FinSightIQ-MarketData-Repo/
├── .dockerignore
├── .editorconfig
├── .env.example
├── .gitignore
├── Dockerfile
├── global.json
├── nuget.config
├── README.md
├── Database/
│   ├── Tables/
│   │   ├── V1__symbols.sql
│   │   ├── V2__stock_prices.sql
│   │   └── V3__provider_health.sql
│   └── Procedures/
│       ├── V4__symbols_procedures.sql
│       ├── V5__stock_prices_procedures.sql
│       └── V6__provider_health_procedures.sql
├── packages/
│   ├── FinSightIQ.BuildingBlocks.0.1.0.nupkg
│   └── FinSightIQ.Contracts.0.1.0.nupkg
└── Application/
    ├── FinSightIQ.MarketDataService.sln
    ├── FinSightIQ.MarketDataService.Api/
    │   ├── Controllers/MarketController.cs
    │   ├── Extensions/
    │   ├── Health/
    │   ├── Middleware/
    │   └── Program.cs
    ├── FinSightIQ.MarketDataService.Core/
    │   ├── DTOs/
    │   ├── Interfaces/
    │   ├── Services/
    │   └── Validators/
    ├── FinSightIQ.MarketDataService.Infrastructure/
    │   ├── Caching/
    │   ├── ExternalApis/
    │   ├── Messaging/
    │   ├── Persistence/
    │   └── Repositories/
    └── FinSightIQ.MarketDataService.Tests/
        ├── Unit/
        ├── Contract/
        └── Integration/
```

---

## 3. Prerequisites

- **.NET 8 SDK** (`8.0.100` or newer; pinned via `global.json` with `latestFeature` roll-forward)
- **PostgreSQL 15+** (with `pg_trgm` extension support)
- **Redis 7+**
- **RabbitMQ 3.12+**
- **Docker Desktop** (for container builds and Testcontainers integration tests)

---

## 4. Configuration & Environment Variables

Copy `.env.example` to `.env` for local configuration. Never commit real secrets or `.env` files.

| Variable | Default / Example | Description |
|----------|-------------------|-------------|
| `ASPNETCORE_URLS` | `http://+:5102` | Bound HTTP listener URL (`5102` locally, `8080` in Docker compose) |
| `ConnectionStrings__MarketDb` | `Host=localhost;Port=5432;Database=marketdb;...` | Primary PostgreSQL connection string for `marketdb` |
| `ConnectionStrings__Postgres` | `Host=localhost;Port=5432;Database=marketdb;...` | Fallback PostgreSQL connection string key |
| `ConnectionStrings__Redis` | `localhost:6379` | Redis connection string for `IDistributedCache` |
| `ConnectionStrings__RabbitMq` | `amqp://guest:guest@localhost:5672` | Optional AMQP URI override for RabbitMQ |
| `RabbitMQ__HostName` | `localhost` | RabbitMQ broker hostname |
| `RabbitMQ__ExchangeName` | `market.price.updated` | Topic exchange for `PriceUpdated` events |
| `MarketData__PollingEnabled` | `true` | Enables/disables `PricePollingService` background polling |
| `MarketData__PollingIntervalSeconds` | `60` | Polling cadence in seconds |
| `MarketData__BypassMarketHoursGuard` | `false` | When `false`, polls only during NYSE regular hours (09:30–16:00 ET) |
| `MarketData__QuoteCacheTtlSeconds` | `60` | Redis TTL in seconds for `finsightiq:market:quote:{SYMBOL}` |
| `MarketData__AlphaVantage__ApiKey` | *(from env)* | Alpha Vantage API key (also reads `ALPHA_VANTAGE_API_KEY`) |
| `MarketData__AlphaVantage__BaseUrl` | `https://www.alphavantage.co/` | Alpha Vantage base URL |
| `MarketData__YahooFinance__BaseUrl` | `https://query1.finance.yahoo.com/` | Yahoo Finance chart API base URL |

---

## 5. Database Migrations (Flyway)

Migrations are located in `Database/Tables/` (`V1`–`V3`) and `Database/Procedures/` (`V4`–`V6`):

```powershell
docker run --rm `
  --network finsightiq-network `
  -v "${PWD}/Database/Tables:/flyway/sql/tables:ro" `
  -v "${PWD}/Database/Procedures:/flyway/sql/procedures:ro" `
  flyway/flyway:10-alpine `
  -url=jdbc:postgresql://postgres:5432/marketdb `
  -user=postgres `
  -password=postgres_dev_password `
  -locations=filesystem:/flyway/sql/tables,filesystem:/flyway/sql/procedures `
  migrate
```

---

## 6. Building, Running & Testing

### Restore & Build
```powershell
dotnet restore Application/FinSightIQ.MarketDataService.sln --configfile nuget.config
dotnet build Application/FinSightIQ.MarketDataService.sln --no-restore
```

### Run Unit, Contract & Integration Tests
```powershell
# Run all tests (requires Docker running for Testcontainers PostgreSQL integration tests)
dotnet test Application/FinSightIQ.MarketDataService.sln --no-build

# Run only unit and contract tests (no Docker required)
dotnet test Application/FinSightIQ.MarketDataService.sln --no-build --filter "FullyQualifiedName!~Integration"
```

### Run Locally
```powershell
dotnet run --project Application/FinSightIQ.MarketDataService.Api/FinSightIQ.MarketDataService.Api.csproj
```
Swagger UI is available at `http://localhost:5102/swagger`.

### Build & Run Docker Container
```powershell
docker build -t finsightiq-market-data-service:local .
docker run --rm -p 5102:5102 --env-file .env.example finsightiq-market-data-service:local
```

---

## 7. REST API Endpoints

| Method | Path | Description | Status Codes |
|--------|------|-------------|--------------|
| `GET` | `/api/v1/market/{symbol}/history` | Cursor-paginated historical OHLCV bars (`from`, `to`, `interval`, `limit`, `cursor`) | `200`, `400`, `404` |
| `GET` | `/api/v1/market/{symbol}/live` | Latest quote with `price`, `change`, `changePercent`, `volume`, `asOf`, `cached` | `200`, `400`, `404`, `503` |
| `GET` | `/api/v1/market/search` | Trigram and ticker prefix search (`?q=App`, min 2 chars, max 20 results) | `200`, `400` |
| `GET` | `/api/v1/market/symbols` | Tracked symbol universe filterable by `exchange`, `sector`, `active`, `limit`, `cursor` | `200`, `400` |
| `GET` | `/health/live` | Liveness probe (process responsiveness only) | `200` |
| `GET` | `/health/ready` | Readiness probe (verifies PostgreSQL `marketdb` and Redis connectivity) | `200`, `503` |
| `GET` | `/health` | Readiness summary alias | `200`, `503` |
| `GET` | `/metrics` | Prometheus metrics scrape endpoint | `200` |

All error responses adhere strictly to **RFC 7807 Problem Details** (`application/problem+json; charset=utf-8`) with `type`, `title`, `status`, `code`, `detail`, `instance`, `traceId`, and `errors`.

---

## 8. Troubleshooting

- **`503 UPSTREAM_UNAVAILABLE` on `/api/v1/market/{symbol}/live`:** Verify that either `stock_prices` has historical/seeded rows for the symbol or a valid `MarketData__AlphaVantage__ApiKey` is configured and network egress is available.
- **PostgreSQL `23514` partition error:** Indicates a bar's `recorded_at` timestamp falls outside provisioned monthly partitions (`2020-01` through `2027-12`). Call `CALL sp_EnsureStockPricePartition(year, month);` to provision additional partitions.
- **Redis unavailable:** The service logs a warning and automatically serves live quotes from PostgreSQL (`cached: false`).
