-- ==============================================================================
-- Functions and Stored Procedures: Symbols
-- Database: marketdb
-- Service: FinSightIQ Market Data Service
-- ==============================================================================

-- GET: Retrieve symbol by ticker code (case-insensitive)
CREATE OR REPLACE FUNCTION fn_GetSymbolByCode(p_symbol VARCHAR(10))
RETURNS TABLE (
    symbol VARCHAR(10),
    name VARCHAR(200),
    exchange VARCHAR(20),
    sector VARCHAR(80),
    currency CHAR(3),
    is_active BOOLEAN,
    first_traded DATE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT s.symbol, s.name, s.exchange, s.sector, s.currency, s.is_active, s.first_traded
    FROM symbols s
    WHERE s.symbol = UPPER(TRIM(p_symbol))
    LIMIT 1;
END;
$$;

-- GET: Search active symbols by ticker prefix or company name trigram similarity
CREATE OR REPLACE FUNCTION fn_SearchSymbols(
    p_query TEXT,
    p_limit INT DEFAULT 20
)
RETURNS TABLE (
    symbol VARCHAR(10),
    name VARCHAR(200),
    exchange VARCHAR(20),
    sector VARCHAR(80),
    currency CHAR(3),
    is_active BOOLEAN,
    first_traded DATE
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_clean_query TEXT := TRIM(p_query);
    v_upper_query TEXT := UPPER(TRIM(p_query));
    v_effective_limit INT := LEAST(GREATEST(COALESCE(p_limit, 20), 1), 20);
BEGIN
    RETURN QUERY
    SELECT s.symbol, s.name, s.exchange, s.sector, s.currency, s.is_active, s.first_traded
    FROM symbols s
    WHERE s.is_active = true
      AND (
          s.symbol ILIKE v_clean_query || '%'
          OR s.name ILIKE '%' || v_clean_query || '%'
          OR similarity(s.name, v_clean_query) > 0.15
      )
    ORDER BY
        CASE
            WHEN s.symbol = v_upper_query THEN 0
            WHEN s.symbol ILIKE v_clean_query || '%' THEN 1
            WHEN s.name ILIKE v_clean_query || '%' THEN 2
            ELSE 3
        END ASC,
        similarity(s.name, v_clean_query) DESC,
        s.symbol ASC
    LIMIT v_effective_limit;
END;
$$;

-- GET: Retrieve filtered and cursor-paginated symbols
CREATE OR REPLACE FUNCTION fn_GetSymbols(
    p_exchange VARCHAR(20) DEFAULT NULL,
    p_sector VARCHAR(80) DEFAULT NULL,
    p_is_active BOOLEAN DEFAULT NULL,
    p_cursor_symbol VARCHAR(10) DEFAULT NULL,
    p_limit INT DEFAULT 50
)
RETURNS TABLE (
    symbol VARCHAR(10),
    name VARCHAR(200),
    exchange VARCHAR(20),
    sector VARCHAR(80),
    currency CHAR(3),
    is_active BOOLEAN,
    first_traded DATE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT s.symbol, s.name, s.exchange, s.sector, s.currency, s.is_active, s.first_traded
    FROM symbols s
    WHERE (p_exchange IS NULL OR UPPER(s.exchange) = UPPER(p_exchange))
      AND (p_sector IS NULL OR LOWER(s.sector) = LOWER(p_sector))
      AND (p_is_active IS NULL OR s.is_active = p_is_active)
      AND (p_cursor_symbol IS NULL OR s.symbol > UPPER(p_cursor_symbol))
    ORDER BY s.symbol ASC
    LIMIT p_limit;
END;
$$;

-- GET: Retrieve all active symbols for background polling
CREATE OR REPLACE FUNCTION fn_GetActiveSymbols()
RETURNS TABLE (
    symbol VARCHAR(10),
    name VARCHAR(200),
    exchange VARCHAR(20),
    sector VARCHAR(80),
    currency CHAR(3),
    is_active BOOLEAN,
    first_traded DATE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT s.symbol, s.name, s.exchange, s.sector, s.currency, s.is_active, s.first_traded
    FROM symbols s
    WHERE s.is_active = true
    ORDER BY s.symbol ASC;
END;
$$;

-- POST/PUT: Upsert a symbol record
CREATE OR REPLACE FUNCTION fn_UpsertSymbol(
    p_symbol VARCHAR(10),
    p_name VARCHAR(200),
    p_exchange VARCHAR(20),
    p_sector VARCHAR(80),
    p_currency CHAR(3),
    p_is_active BOOLEAN,
    p_first_traded DATE
)
RETURNS TABLE (
    symbol VARCHAR(10),
    name VARCHAR(200),
    exchange VARCHAR(20),
    sector VARCHAR(80),
    currency CHAR(3),
    is_active BOOLEAN,
    first_traded DATE
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    RETURN QUERY
    INSERT INTO symbols (
        symbol, name, exchange, sector, currency, is_active, first_traded
    ) VALUES (
        UPPER(TRIM(p_symbol)),
        TRIM(p_name),
        UPPER(TRIM(p_exchange)),
        p_sector,
        COALESCE(UPPER(TRIM(p_currency)), 'USD'),
        COALESCE(p_is_active, true),
        p_first_traded
    )
    ON CONFLICT ON CONSTRAINT symbols_pkey DO UPDATE
    SET name = EXCLUDED.name,
        exchange = EXCLUDED.exchange,
        sector = EXCLUDED.sector,
        currency = EXCLUDED.currency,
        is_active = EXCLUDED.is_active,
        first_traded = COALESCE(EXCLUDED.first_traded, symbols.first_traded)
    RETURNING symbols.symbol, symbols.name, symbols.exchange, symbols.sector, symbols.currency, symbols.is_active, symbols.first_traded;
END;
$$;
