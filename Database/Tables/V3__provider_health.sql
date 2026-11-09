-- =============================================================================
-- Migration: V3__provider_health.sql
-- Purpose  : Create provider_health table for upstream circuit state and
--            failure tracking, and seed AlphaVantage & YahooFinance rows.
-- =============================================================================

CREATE TABLE IF NOT EXISTS provider_health (
    provider             VARCHAR(20) PRIMARY KEY,
    consecutive_failures INT         NOT NULL DEFAULT 0,
    circuit_state        VARCHAR(10) NOT NULL DEFAULT 'Closed',
    last_success_at      TIMESTAMPTZ NULL,
    last_failure_at      TIMESTAMPTZ NULL,
    last_error           TEXT        NULL,
    CONSTRAINT ck_provider_circuit_state CHECK (circuit_state IN ('Closed', 'Open', 'HalfOpen')),
    CONSTRAINT ck_provider_consecutive_failures CHECK (consecutive_failures >= 0)
);

INSERT INTO provider_health (provider, consecutive_failures, circuit_state)
VALUES
    ('AlphaVantage', 0, 'Closed'),
    ('YahooFinance', 0, 'Closed')
ON CONFLICT (provider) DO NOTHING;

