-- =============================================================================
-- Migration: V2__stock_prices.sql
-- Purpose  : Create partitioned stock_prices table, OHLCV constraints,
--            query and idempotency indexes, and monthly range partitions.
-- =============================================================================

CREATE TABLE IF NOT EXISTS stock_prices (
    id          BIGSERIAL,
    symbol      VARCHAR(10)     NOT NULL,
    open        NUMERIC(12,4)   NOT NULL,
    high        NUMERIC(12,4)   NOT NULL,
    low         NUMERIC(12,4)   NOT NULL,
    close       NUMERIC(12,4)   NOT NULL,
    volume      BIGINT          NOT NULL,
    interval    VARCHAR(10)     NOT NULL DEFAULT '1day',
    source      VARCHAR(20)     NOT NULL,
    recorded_at TIMESTAMPTZ     NOT NULL,
    ingested_at TIMESTAMPTZ     NOT NULL DEFAULT now(),
    PRIMARY KEY (id, recorded_at),
    CONSTRAINT ck_prices_hl   CHECK (high >= low),
    CONSTRAINT ck_prices_band CHECK (high >= open AND high >= close AND low <= open AND low <= close),
    CONSTRAINT ck_prices_vol  CHECK (volume >= 0)
) PARTITION BY RANGE (recorded_at);

CREATE INDEX IF NOT EXISTS ix_prices_symbol_time
    ON stock_prices (symbol, recorded_at DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ux_prices_symbol_interval_time
    ON stock_prices (symbol, interval, recorded_at);

-- Create monthly partitions from 2020-01 through 2027-12 to support
-- 5+ years of historical seed data and current/upcoming ingestion windows.
DO $$
DECLARE
    v_year        INT;
    v_month       INT;
    v_start_date  TIMESTAMPTZ;
    v_end_date    TIMESTAMPTZ;
    v_part_name   TEXT;
BEGIN
    FOR v_year IN 2020..2027 LOOP
        FOR v_month IN 1..12 LOOP
            v_start_date := make_timestamptz(v_year, v_month, 1, 0, 0, 0, 'UTC');
            v_end_date   := v_start_date + INTERVAL '1 month';
            v_part_name  := format('stock_prices_%s_%s', v_year, lpad(v_month::text, 2, '0'));

            EXECUTE format(
                'CREATE TABLE IF NOT EXISTS %I PARTITION OF stock_prices FOR VALUES FROM (%L) TO (%L)',
                v_part_name,
                v_start_date,
                v_end_date
            );
        END LOOP;
    END LOOP;
END $$;

