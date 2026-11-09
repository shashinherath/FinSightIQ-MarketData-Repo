-- =============================================================================
-- Migration: V1__symbols.sql
-- Purpose  : Create pg_trgm extension, symbols table, trigram search index,
--            and seed the initial 30 S&P 500 equity universe.
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS symbols (
    symbol        VARCHAR(10)   PRIMARY KEY,
    name          VARCHAR(200)  NOT NULL,
    exchange      VARCHAR(20)   NOT NULL,
    sector        VARCHAR(80)   NULL,
    currency      CHAR(3)       NOT NULL DEFAULT 'USD',
    is_active     BOOLEAN       NOT NULL DEFAULT true,
    first_traded  DATE          NULL
);

CREATE INDEX IF NOT EXISTS ix_symbols_name_trgm
    ON symbols USING gin (name gin_trgm_ops);

CREATE INDEX IF NOT EXISTS ix_symbols_active_exchange_sector
    ON symbols (is_active, exchange, sector);

INSERT INTO symbols (symbol, name, sector, exchange, currency, is_active, first_traded) VALUES
    ('AAPL',  'Apple Inc.',                        'Technology',             'NASDAQ', 'USD', true, '1980-12-12'),
    ('MSFT',  'Microsoft Corporation',             'Technology',             'NASDAQ', 'USD', true, '1986-03-13'),
    ('GOOGL', 'Alphabet Inc. Class A',             'Communication Services', 'NASDAQ', 'USD', true, '2004-08-19'),
    ('AMZN',  'Amazon.com Inc.',                   'Consumer Discretionary', 'NASDAQ', 'USD', true, '1997-05-15'),
    ('NVDA',  'NVIDIA Corporation',                'Technology',             'NASDAQ', 'USD', true, '1999-01-22'),
    ('META',  'Meta Platforms Inc.',               'Communication Services', 'NASDAQ', 'USD', true, '2012-05-18'),
    ('TSLA',  'Tesla Inc.',                        'Consumer Discretionary', 'NASDAQ', 'USD', true, '2010-06-29'),
    ('BRK.B', 'Berkshire Hathaway Inc. Class B',   'Financials',             'NYSE',   'USD', true, '1996-05-09'),
    ('JPM',   'JPMorgan Chase & Co.',              'Financials',             'NYSE',   'USD', true, '1969-03-05'),
    ('V',     'Visa Inc.',                         'Financials',             'NYSE',   'USD', true, '2008-03-19'),
    ('JNJ',   'Johnson & Johnson',                 'Healthcare',             'NYSE',   'USD', true, '1944-09-25'),
    ('UNH',   'UnitedHealth Group Inc.',           'Healthcare',             'NYSE',   'USD', true, '1984-10-17'),
    ('WMT',   'Walmart Inc.',                      'Consumer Staples',       'NYSE',   'USD', true, '1972-08-25'),
    ('PG',    'Procter & Gamble Co.',              'Consumer Staples',       'NYSE',   'USD', true, '1970-01-02'),
    ('MA',    'Mastercard Inc.',                   'Financials',             'NYSE',   'USD', true, '2006-05-25'),
    ('HD',    'Home Depot Inc.',                   'Consumer Discretionary', 'NYSE',   'USD', true, '1981-09-22'),
    ('XOM',   'Exxon Mobil Corporation',           'Energy',                 'NYSE',   'USD', true, '1970-01-02'),
    ('CVX',   'Chevron Corporation',               'Energy',                 'NYSE',   'USD', true, '1970-01-02'),
    ('MRK',   'Merck & Co. Inc.',                  'Healthcare',             'NYSE',   'USD', true, '1970-01-02'),
    ('LLY',   'Eli Lilly and Company',             'Healthcare',             'NYSE',   'USD', true, '1970-01-02'),
    ('ABBV',  'AbbVie Inc.',                       'Healthcare',             'NYSE',   'USD', true, '2013-01-02'),
    ('PEP',   'PepsiCo Inc.',                      'Consumer Staples',       'NASDAQ', 'USD', true, '1972-06-01'),
    ('KO',    'Coca-Cola Company',                 'Consumer Staples',       'NYSE',   'USD', true, '1970-01-02'),
    ('COST',  'Costco Wholesale Corporation',      'Consumer Staples',       'NASDAQ', 'USD', true, '1985-12-05'),
    ('AVGO',  'Broadcom Inc.',                     'Technology',             'NASDAQ', 'USD', true, '2009-08-06'),
    ('TMO',   'Thermo Fisher Scientific Inc.',     'Healthcare',             'NYSE',   'USD', true, '1980-08-29'),
    ('ORCL',  'Oracle Corporation',                'Technology',             'NYSE',   'USD', true, '1986-03-12'),
    ('CRM',   'Salesforce Inc.',                   'Technology',             'NYSE',   'USD', true, '2004-06-23'),
    ('AMD',   'Advanced Micro Devices Inc.',       'Technology',             'NASDAQ', 'USD', true, '1972-09-27'),
    ('NFLX',  'Netflix Inc.',                      'Communication Services', 'NASDAQ', 'USD', true, '2002-05-23')
ON CONFLICT (symbol) DO NOTHING;

