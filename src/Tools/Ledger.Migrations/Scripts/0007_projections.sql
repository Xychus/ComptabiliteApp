-- Modèles de lecture (phase 4, ADR 0015 à 0018). Tout ce schéma est dérivé de ledger.events et
-- reconstructible par rejeu (invariant 2) : aucune donnée n'y vient d'ailleurs, aucune colonne
-- n'y prend l'heure du serveur.

-- Le plan comptable est tenu dans le journal (ADR 0012) ; la table de référence n'est plus lue.
-- Sa forme lisible est la projection projections.chart_of_accounts.
DROP TABLE ledger.accounts;

-- Rôle de l'API de requête : lecture seule des projections et de la tête du journal (fraîcheur).
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ledger_query') THEN
        CREATE ROLE ledger_query NOLOGIN;
    END IF;

    -- Les tables du schéma appartiennent à ledger_projection (rejeu par renommage, ADR 0016).
    -- Hors superutilisateur (Aurora), le rôle qui migre doit pouvoir endosser ce rôle.
    IF NOT pg_has_role(current_user, 'ledger_projection', 'MEMBER') THEN
        EXECUTE format('GRANT ledger_projection TO %I', current_user);
    END IF;
END
$$;

GRANT USAGE ON SCHEMA ledger TO ledger_query;
GRANT SELECT ON ledger.events TO ledger_query;

CREATE SCHEMA projections;
REVOKE ALL ON SCHEMA projections FROM PUBLIC;
-- CREATE : tables parallèles du rejeu. Le service de commandes n'a aucun droit sur ce schéma.
GRANT USAGE, CREATE ON SCHEMA projections TO ledger_projection;
GRANT USAGE ON SCHEMA projections TO ledger_query;

-- Checkpoint du consommateur Kafka, écrit dans la transaction des modèles de lecture (ADR 0015).
CREATE TABLE projections.consumer_offsets (
    consumer_group TEXT    NOT NULL,
    topic          TEXT    NOT NULL,
    partition      INTEGER NOT NULL,
    next_offset    BIGINT  NOT NULL,
    CONSTRAINT consumer_offsets_pkey PRIMARY KEY (consumer_group, topic, partition),
    CONSTRAINT consumer_offsets_next_offset_positive CHECK (next_offset >= 0)
);

-- Position de chaque jeu de tables (projection en service, ou en reconstruction : suffixe __rebuild).
CREATE TABLE projections.projection_positions (
    slot             TEXT        NOT NULL,
    through_sequence BIGINT      NOT NULL,
    last_recorded_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT projection_positions_pkey PRIMARY KEY (slot)
);

-- Événements appliqués, par projection : idempotence par event_id (ADR 0015).
DO $$
DECLARE
    projection_name TEXT;
BEGIN
    FOREACH projection_name IN ARRAY ARRAY['account_balances', 'ledger_entries', 'chart_of_accounts', 'periods', 'period_snapshots', 'tax_projection'] LOOP
        EXECUTE format(
            'CREATE TABLE projections.%I (event_id UUID NOT NULL, global_sequence BIGINT NOT NULL, CONSTRAINT %I PRIMARY KEY (event_id))',
            projection_name || '_applied',
            projection_name || '_applied_pkey');
    END LOOP;
END
$$;

-- Soldes courants : débits et crédits cumulés par compte et devise.
CREATE TABLE projections.account_balances (
    entity_id    TEXT           NOT NULL,
    account_id   TEXT           NOT NULL,
    currency     CHAR(3)        NOT NULL,
    debit_total  NUMERIC(20, 2) NOT NULL,
    credit_total NUMERIC(20, 2) NOT NULL,
    CONSTRAINT account_balances_pkey PRIMARY KEY (entity_id, account_id, currency)
);

-- Grand livre : écritures et lignes ; sous-livre d'un compte dans l'ordre comptable.
CREATE TABLE projections.journal_entries (
    entry_id        UUID             NOT NULL,
    entity_id       TEXT             NOT NULL,
    business_date   DATE             NOT NULL,
    period          TEXT COLLATE "C" NOT NULL,
    description     TEXT             NULL,
    reversal_of     UUID             NULL,
    reversed_by     UUID             NULL,
    global_sequence BIGINT           NOT NULL,
    recorded_at     TIMESTAMPTZ      NOT NULL,
    actor           TEXT             NOT NULL,
    CONSTRAINT journal_entries_pkey PRIMARY KEY (entry_id)
);

CREATE TABLE projections.ledger_lines (
    entry_id        UUID             NOT NULL,
    line_number     INTEGER          NOT NULL,
    entity_id       TEXT             NOT NULL,
    account_id      TEXT             NOT NULL,
    side            TEXT             NOT NULL,
    amount          NUMERIC(20, 2)   NOT NULL,
    currency        CHAR(3)          NOT NULL,
    business_date   DATE             NOT NULL,
    period          TEXT COLLATE "C" NOT NULL,
    global_sequence BIGINT           NOT NULL,
    CONSTRAINT ledger_lines_pkey PRIMARY KEY (entry_id, line_number),
    CONSTRAINT ledger_lines_side CHECK (side IN ('Debit', 'Credit'))
);

-- Pagination par curseur du sous-livre d'un compte : jamais d'ordre par UUID.
CREATE INDEX ledger_lines_account_order ON projections.ledger_lines (entity_id, account_id, business_date, global_sequence, line_number);

-- Plan comptable lisible (ADR 0012).
CREATE TABLE projections.chart_of_accounts (
    entity_id           TEXT        NOT NULL,
    account_id          TEXT        NOT NULL,
    name                TEXT        NOT NULL,
    currency            CHAR(3)     NOT NULL,
    normal_side         TEXT        NOT NULL,
    is_active           BOOLEAN     NOT NULL,
    no_negative_balance BOOLEAN     NOT NULL,
    chart_version       BIGINT      NOT NULL,
    defined_at          TIMESTAMPTZ NOT NULL,
    CONSTRAINT chart_of_accounts_pkey PRIMARY KEY (entity_id, account_id),
    CONSTRAINT chart_of_accounts_normal_side CHECK (normal_side IN ('Debit', 'Credit'))
);

-- Statut des périodes et empreinte de leur sceau (ADR 0013).
CREATE TABLE projections.periods (
    entity_id   TEXT             NOT NULL,
    period      TEXT COLLATE "C" NOT NULL,
    status      TEXT             NOT NULL,
    version     BIGINT           NOT NULL,
    opened_at   TIMESTAMPTZ      NULL,
    sealed_at   TIMESTAMPTZ      NULL,
    event_count BIGINT           NULL,
    merkle_root TEXT             NULL,
    CONSTRAINT periods_pkey PRIMARY KEY (entity_id, period),
    CONSTRAINT periods_status CHECK (status IN ('Open', 'Sealed'))
);

-- Instantanés de fin de période, figés au scellement (ADR 0017).
CREATE TABLE projections.period_snapshots (
    entity_id     TEXT             NOT NULL,
    period        TEXT COLLATE "C" NOT NULL,
    account_id    TEXT             NOT NULL,
    currency      CHAR(3)          NOT NULL,
    debits        NUMERIC(20, 2)   NOT NULL,
    credits       NUMERIC(20, 2)   NOT NULL,
    seal_sequence BIGINT           NOT NULL,
    event_count   BIGINT           NOT NULL,
    CONSTRAINT period_snapshots_pkey PRIMARY KEY (entity_id, period, account_id, currency)
);

-- TPS/TVQ perçues et payées, par période (ADR 0018).
CREATE TABLE projections.tax_projection (
    entity_id     TEXT             NOT NULL,
    period        TEXT COLLATE "C" NOT NULL,
    currency      CHAR(3)          NOT NULL,
    gst_collected NUMERIC(20, 2)   NOT NULL,
    gst_paid      NUMERIC(20, 2)   NOT NULL,
    qst_collected NUMERIC(20, 2)   NOT NULL,
    qst_paid      NUMERIC(20, 2)   NOT NULL,
    CONSTRAINT tax_projection_pkey PRIMARY KEY (entity_id, period, currency)
);

-- Propriété : le worker renomme et supprime ses tables au rejeu. Lecture seule pour l'API de requête.
DO $$
DECLARE
    table_name TEXT;
BEGIN
    FOR table_name IN SELECT tablename FROM pg_tables WHERE schemaname = 'projections' LOOP
        EXECUTE format('ALTER TABLE projections.%I OWNER TO ledger_projection', table_name);
        EXECUTE format('GRANT SELECT ON projections.%I TO ledger_query', table_name);
    END LOOP;
END
$$;
