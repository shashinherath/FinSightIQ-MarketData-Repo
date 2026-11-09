-- ==============================================================================
-- Functions and Stored Procedures: Provider Health
-- Database: marketdb
-- Service: FinSightIQ Market Data Service
-- ==============================================================================

-- GET: Retrieve health state for a single upstream provider
CREATE OR REPLACE FUNCTION fn_GetProviderHealth(p_provider VARCHAR(20))
RETURNS TABLE (
    provider VARCHAR(20),
    consecutive_failures INT,
    circuit_state VARCHAR(10),
    last_success_at TIMESTAMPTZ,
    last_failure_at TIMESTAMPTZ,
    last_error TEXT
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        ph.provider,
        ph.consecutive_failures,
        ph.circuit_state,
        ph.last_success_at,
        ph.last_failure_at,
        ph.last_error
    FROM provider_health ph
    WHERE ph.provider = p_provider
    LIMIT 1;
END;
$$;

-- GET: Retrieve health state for all upstream providers
CREATE OR REPLACE FUNCTION fn_GetAllProviderHealth()
RETURNS TABLE (
    provider VARCHAR(20),
    consecutive_failures INT,
    circuit_state VARCHAR(10),
    last_success_at TIMESTAMPTZ,
    last_failure_at TIMESTAMPTZ,
    last_error TEXT
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        ph.provider,
        ph.consecutive_failures,
        ph.circuit_state,
        ph.last_success_at,
        ph.last_failure_at,
        ph.last_error
    FROM provider_health ph
    ORDER BY ph.provider ASC;
END;
$$;

-- PUT: Record successful upstream provider call and reset consecutive failures
CREATE OR REPLACE PROCEDURE sp_RecordProviderSuccess(
    p_provider VARCHAR(20),
    p_succeeded_at TIMESTAMPTZ
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO provider_health (
        provider, consecutive_failures, circuit_state, last_success_at, last_error
    ) VALUES (
        p_provider, 0, 'Closed', COALESCE(p_succeeded_at, now()), NULL
    )
    ON CONFLICT (provider) DO UPDATE
    SET consecutive_failures = 0,
        circuit_state = 'Closed',
        last_success_at = COALESCE(p_succeeded_at, now()),
        last_error = NULL;
END;
$$;

-- PUT: Record upstream provider failure and update circuit state
CREATE OR REPLACE PROCEDURE sp_RecordProviderFailure(
    p_provider VARCHAR(20),
    p_failed_at TIMESTAMPTZ,
    p_error TEXT,
    p_circuit_state VARCHAR(10) DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO provider_health (
        provider,
        consecutive_failures,
        circuit_state,
        last_failure_at,
        last_error
    ) VALUES (
        p_provider,
        1,
        COALESCE(p_circuit_state, 'Closed'),
        COALESCE(p_failed_at, now()),
        p_error
    )
    ON CONFLICT (provider) DO UPDATE
    SET consecutive_failures = provider_health.consecutive_failures + 1,
        circuit_state = COALESCE(
            p_circuit_state,
            CASE
                WHEN provider_health.consecutive_failures + 1 >= 5 THEN 'Open'
                ELSE provider_health.circuit_state
            END
        ),
        last_failure_at = COALESCE(p_failed_at, now()),
        last_error = p_error;
END;
$$;

-- PUT: Explicitly update circuit breaker state ('Closed', 'Open', 'HalfOpen')
CREATE OR REPLACE PROCEDURE sp_UpdateProviderCircuitState(
    p_provider VARCHAR(20),
    p_circuit_state VARCHAR(10)
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO provider_health (
        provider, consecutive_failures, circuit_state
    ) VALUES (
        p_provider, 0, p_circuit_state
    )
    ON CONFLICT (provider) DO UPDATE
    SET circuit_state = p_circuit_state,
        consecutive_failures = CASE
            WHEN p_circuit_state = 'Closed' THEN 0
            ELSE provider_health.consecutive_failures
        END;
END;
$$;

