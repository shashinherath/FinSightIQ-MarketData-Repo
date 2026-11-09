-- ==============================================================================
-- Functions and Stored Procedures: Stock Prices
-- Database: marketdb
-- Service: FinSightIQ Market Data Service
-- ==============================================================================

-- GET: Retrieve cursor-paginated historical OHLCV bars ordered by (recorded_at ASC, id ASC)
CREATE OR REPLACE FUNCTION fn_GetHistoricalPrices(
    p_symbol VARCHAR(10),
    p_interval VARCHAR(10),
    p_from TIMESTAMPTZ DEFAULT NULL,
    p_to TIMESTAMPTZ DEFAULT NULL,
    p_cursor_recorded_at TIMESTAMPTZ DEFAULT NULL,
    p_cursor_id BIGINT DEFAULT NULL,
    p_limit INT DEFAULT 51
)
RETURNS TABLE (
    id BIGINT,
    symbol VARCHAR(10),
    open NUMERIC(12,4),
    high NUMERIC(12,4),
    low NUMERIC(12,4),
    close NUMERIC(12,4),
    volume BIGINT,
    "interval" VARCHAR(10),
    source VARCHAR(20),
    recorded_at TIMESTAMPTZ,
    ingested_at TIMESTAMPTZ
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        sp.id,
        sp.symbol,
        sp.open,
        sp.high,
        sp.low,
        sp.close,
        sp.volume,
        sp."interval",
        sp.source,
        sp.recorded_at,
        sp.ingested_at
    FROM stock_prices sp
    WHERE sp.symbol = UPPER(TRIM(p_symbol))
      AND sp."interval" = p_interval
      AND (p_from IS NULL OR sp.recorded_at >= p_from)
      AND (p_to IS NULL OR sp.recorded_at <= p_to)
      AND (
          p_cursor_recorded_at IS NULL
          OR sp.recorded_at > p_cursor_recorded_at
          OR (sp.recorded_at = p_cursor_recorded_at AND (p_cursor_id IS NULL OR sp.id > p_cursor_id))
      )
    ORDER BY sp.recorded_at ASC, sp.id ASC
    LIMIT p_limit;
END;
$$;

-- GET: Retrieve the latest N bars for a symbol (used for live quote and previous close computation)
CREATE OR REPLACE FUNCTION fn_GetLatestStockPrices(
    p_symbol VARCHAR(10),
    p_interval VARCHAR(10) DEFAULT NULL,
    p_count INT DEFAULT 2
)
RETURNS TABLE (
    id BIGINT,
    symbol VARCHAR(10),
    open NUMERIC(12,4),
    high NUMERIC(12,4),
    low NUMERIC(12,4),
    close NUMERIC(12,4),
    volume BIGINT,
    "interval" VARCHAR(10),
    source VARCHAR(20),
    recorded_at TIMESTAMPTZ,
    ingested_at TIMESTAMPTZ
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        sp.id,
        sp.symbol,
        sp.open,
        sp.high,
        sp.low,
        sp.close,
        sp.volume,
        sp."interval",
        sp.source,
        sp.recorded_at,
        sp.ingested_at
    FROM stock_prices sp
    WHERE sp.symbol = UPPER(TRIM(p_symbol))
      AND (p_interval IS NULL OR sp."interval" = p_interval)
    ORDER BY sp.recorded_at DESC, sp.id DESC
    LIMIT p_count;
END;
$$;

-- POST: Upsert a single OHLCV bar idempotently on (symbol, interval, recorded_at)
CREATE OR REPLACE FUNCTION fn_UpsertStockPrice(
    p_symbol VARCHAR(10),
    p_open NUMERIC(12,4),
    p_high NUMERIC(12,4),
    p_low NUMERIC(12,4),
    p_close NUMERIC(12,4),
    p_volume BIGINT,
    p_interval VARCHAR(10),
    p_source VARCHAR(20),
    p_recorded_at TIMESTAMPTZ,
    p_ingested_at TIMESTAMPTZ
)
RETURNS TABLE (
    id BIGINT,
    symbol VARCHAR(10),
    open NUMERIC(12,4),
    high NUMERIC(12,4),
    low NUMERIC(12,4),
    close NUMERIC(12,4),
    volume BIGINT,
    "interval" VARCHAR(10),
    source VARCHAR(20),
    recorded_at TIMESTAMPTZ,
    ingested_at TIMESTAMPTZ
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    RETURN QUERY
    INSERT INTO stock_prices (
        symbol, open, high, low, close, volume, "interval", source, recorded_at, ingested_at
    ) VALUES (
        UPPER(TRIM(p_symbol)),
        p_open,
        p_high,
        p_low,
        p_close,
        p_volume,
        p_interval,
        p_source,
        p_recorded_at,
        COALESCE(p_ingested_at, now())
    )
    ON CONFLICT (symbol, "interval", recorded_at) DO UPDATE
    SET open = EXCLUDED.open,
        high = EXCLUDED.high,
        low = EXCLUDED.low,
        close = EXCLUDED.close,
        volume = EXCLUDED.volume,
        source = EXCLUDED.source,
        ingested_at = EXCLUDED.ingested_at
    RETURNING
        stock_prices.id,
        stock_prices.symbol,
        stock_prices.open,
        stock_prices.high,
        stock_prices.low,
        stock_prices.close,
        stock_prices.volume,
        stock_prices."interval",
        stock_prices.source,
        stock_prices.recorded_at,
        stock_prices.ingested_at;
END;
$$;

-- PROCEDURE: Ensure monthly range partition stock_prices_YYYY_MM exists
CREATE OR REPLACE PROCEDURE sp_EnsureStockPricePartition(
    p_year INT,
    p_month INT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_start_date TIMESTAMPTZ;
    v_end_date   TIMESTAMPTZ;
    v_part_name  TEXT;
BEGIN
    v_start_date := make_timestamptz(p_year, p_month, 1, 0, 0, 0, 'UTC');
    v_end_date   := v_start_date + INTERVAL '1 month';
    v_part_name  := format('stock_prices_%s_%s', p_year, lpad(p_month::text, 2, '0'));

    EXECUTE format(
        'CREATE TABLE IF NOT EXISTS %I PARTITION OF stock_prices FOR VALUES FROM (%L) TO (%L)',
        v_part_name,
        v_start_date,
        v_end_date
    );
END;
$$;
