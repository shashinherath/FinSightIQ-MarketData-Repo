using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using Npgsql;
using NpgsqlTypes;

namespace FinSightIQ.MarketDataService.Infrastructure.Repositories;

/// <summary>
/// Dapper + NpgsqlBinaryImporter repository implementation for the partitioned stock_prices table.
/// </summary>
public sealed class StockPriceRepository : IStockPriceRepository
{
    private readonly IDapperWrapper _dapper;

    public StockPriceRepository(IDapperWrapper dapper)
    {
        ArgumentNullException.ThrowIfNull(dapper);
        _dapper = dapper;
    }

    public async Task<IReadOnlyList<StockPriceEntity>> GetHistoricalPricesAsync(
        string symbol,
        string interval,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        DateTimeOffset? cursorRecordedAt = null,
        long? cursorId = null,
        int limit = 51,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(interval);

        const string sql = @"
            SELECT * FROM fn_GetHistoricalPrices(
                @p_symbol,
                @p_interval,
                @p_from,
                @p_to,
                @p_cursor_recorded_at,
                @p_cursor_id,
                @p_limit
            );";

        IEnumerable<StockPriceEntity> rows = await _dapper.QueryAsync<StockPriceEntity>(
            sql,
            new
            {
                p_symbol = symbol.Trim().ToUpperInvariant(),
                p_interval = interval.Trim(),
                p_from = from?.ToUniversalTime(),
                p_to = to?.ToUniversalTime(),
                p_cursor_recorded_at = cursorRecordedAt?.ToUniversalTime(),
                p_cursor_id = cursorId,
                p_limit = limit
            },
            cancellationToken: cancellationToken);

        return rows.ToList();
    }

    public async Task<IReadOnlyList<StockPriceEntity>> GetLatestPricesAsync(
        string symbol,
        string? interval = null,
        int count = 2,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        const string sql = "SELECT * FROM fn_GetLatestStockPrices(@p_symbol, @p_interval, @p_count);";

        IEnumerable<StockPriceEntity> rows = await _dapper.QueryAsync<StockPriceEntity>(
            sql,
            new
            {
                p_symbol = symbol.Trim().ToUpperInvariant(),
                p_interval = string.IsNullOrWhiteSpace(interval) ? null : interval.Trim(),
                p_count = count
            },
            cancellationToken: cancellationToken);

        return rows.ToList();
    }

    public async Task<StockPriceEntity> UpsertAsync(
        OhlcvBarDto bar,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bar);

        const string sql = @"
            SELECT * FROM fn_UpsertStockPrice(
                @p_symbol,
                @p_open,
                @p_high,
                @p_low,
                @p_close,
                @p_volume,
                @p_interval,
                @p_source,
                @p_recorded_at,
                @p_ingested_at
            );";

        StockPriceEntity? entity = await _dapper.QuerySingleOrDefaultAsync<StockPriceEntity>(
            sql,
            new
            {
                p_symbol = bar.Symbol.Trim().ToUpperInvariant(),
                p_open = bar.Open,
                p_high = bar.High,
                p_low = bar.Low,
                p_close = bar.Close,
                p_volume = bar.Volume,
                p_interval = bar.Interval.Trim(),
                p_source = bar.Source.Trim(),
                p_recorded_at = bar.RecordedAt.ToUniversalTime(),
                p_ingested_at = DateTimeOffset.UtcNow
            },
            cancellationToken: cancellationToken);

        return entity ?? throw new InvalidOperationException($"Failed to upsert stock price bar for '{bar.Symbol}'.");
    }

    public async Task<int> BulkUpsertAsync(
        IReadOnlyList<OhlcvBarDto> bars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (bars.Count == 0)
        {
            return 0;
        }

        await using NpgsqlConnection connection = new NpgsqlConnection(_dapper.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string createStagingSql = @"
            CREATE TEMP TABLE tmp_stock_prices_ingest (
                symbol      VARCHAR(10)   NOT NULL,
                open        NUMERIC(12,4) NOT NULL,
                high        NUMERIC(12,4) NOT NULL,
                low         NUMERIC(12,4) NOT NULL,
                close       NUMERIC(12,4) NOT NULL,
                volume      BIGINT        NOT NULL,
                ""interval"" VARCHAR(10)  NOT NULL,
                source      VARCHAR(20)   NOT NULL,
                recorded_at TIMESTAMPTZ   NOT NULL,
                ingested_at TIMESTAMPTZ   NOT NULL
            ) ON COMMIT DROP;";

        await using (NpgsqlCommand createCmd = new NpgsqlCommand(createStagingSql, connection, transaction))
        {
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        const string copySql = @"
            COPY tmp_stock_prices_ingest (
                symbol, open, high, low, close, volume, ""interval"", source, recorded_at, ingested_at
            ) FROM STDIN (FORMAT BINARY)";

        DateTimeOffset ingestedAtUtc = DateTimeOffset.UtcNow;

        await using (NpgsqlBinaryImporter importer = await connection.BeginBinaryImportAsync(copySql, cancellationToken))
        {
            foreach (OhlcvBarDto bar in bars)
            {
                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(bar.Symbol.Trim().ToUpperInvariant(), NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(bar.Open, NpgsqlDbType.Numeric, cancellationToken);
                await importer.WriteAsync(bar.High, NpgsqlDbType.Numeric, cancellationToken);
                await importer.WriteAsync(bar.Low, NpgsqlDbType.Numeric, cancellationToken);
                await importer.WriteAsync(bar.Close, NpgsqlDbType.Numeric, cancellationToken);
                await importer.WriteAsync(bar.Volume, NpgsqlDbType.Bigint, cancellationToken);
                await importer.WriteAsync(bar.Interval.Trim(), NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(bar.Source.Trim(), NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(bar.RecordedAt.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                await importer.WriteAsync(ingestedAtUtc, NpgsqlDbType.TimestampTz, cancellationToken);
            }

            await importer.CompleteAsync(cancellationToken);
        }

        const string mergeSql = @"
            INSERT INTO stock_prices (
                symbol, open, high, low, close, volume, ""interval"", source, recorded_at, ingested_at
            )
            SELECT DISTINCT ON (symbol, ""interval"", recorded_at)
                symbol, open, high, low, close, volume, ""interval"", source, recorded_at, ingested_at
            FROM tmp_stock_prices_ingest
            ORDER BY symbol, ""interval"", recorded_at, ingested_at DESC
            ON CONFLICT (symbol, ""interval"", recorded_at) DO UPDATE
            SET open        = EXCLUDED.open,
                high        = EXCLUDED.high,
                low         = EXCLUDED.low,
                close       = EXCLUDED.close,
                volume      = EXCLUDED.volume,
                source      = EXCLUDED.source,
                ingested_at = EXCLUDED.ingested_at;";

        int affectedRows;
        await using (NpgsqlCommand mergeCmd = new NpgsqlCommand(mergeSql, connection, transaction))
        {
            affectedRows = await mergeCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return affectedRows;
    }

    public async Task EnsurePartitionExistsAsync(
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        const string sql = "CALL sp_EnsureStockPricePartition(@p_year, @p_month);";
        await _dapper.ExecuteAsync(
            sql,
            new { p_year = year, p_month = month },
            cancellationToken: cancellationToken);
    }
}

