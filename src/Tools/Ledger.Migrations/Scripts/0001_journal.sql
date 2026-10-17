-- Journal d'événements : seule source de vérité, append-only (ADR 0007).
-- Ne jamais modifier ce script une fois fusionné : ajouter une nouvelle migration.

CREATE SCHEMA IF NOT EXISTS ledger;

-- Ordre d'insertion (autorité serveur), imposé par le trigger ledger.guard_event_insert (0002).
CREATE SEQUENCE ledger.events_global_sequence AS BIGINT;

CREATE TABLE ledger.events (
    global_sequence BIGINT         NOT NULL DEFAULT nextval('ledger.events_global_sequence'),
    event_id        UUID           NOT NULL,
    stream_id       TEXT           NOT NULL,
    stream_version  BIGINT         NOT NULL,
    event_type      TEXT           NOT NULL,
    schema_version  INTEGER        NOT NULL,
    entity_id       TEXT           NOT NULL,
    business_date   DATE           NULL,
    recorded_at     TIMESTAMPTZ    NOT NULL,
    period          TEXT COLLATE "C" NOT NULL,
    payload         JSONB          NOT NULL,
    balance_after   NUMERIC(20, 2) NULL,
    correlation_id  UUID           NOT NULL,
    causation_id    UUID           NULL,
    actor           TEXT           NOT NULL,
    hash            TEXT           NOT NULL,
    previous_hash   TEXT           NULL,
    -- PostgreSQL exige la clé de partition dans toute contrainte d'unicité : l'unicité globale
    -- (flux, version) et (event_id) est portée par ledger.stream_versions.
    CONSTRAINT events_pkey PRIMARY KEY (global_sequence, period),
    CONSTRAINT events_stream_version_positive CHECK (stream_version >= 1),
    CONSTRAINT events_schema_version_positive CHECK (schema_version >= 1),
    CONSTRAINT events_period_format CHECK (period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'),
    CONSTRAINT events_business_date_in_period CHECK (business_date IS NULL OR to_char(business_date, 'YYYY-MM') = period),
    CONSTRAINT events_payload_object CHECK (jsonb_typeof(payload) = 'object'),
    CONSTRAINT events_hash_format CHECK (hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT events_previous_hash_format CHECK (previous_hash ~ '^[0-9a-f]{64}$'),
    -- Chaînage par flux (ADR 0005) : seule la version 1 n'a pas de prédécesseur.
    CONSTRAINT events_chain_start CHECK ((stream_version = 1) = (previous_hash IS NULL))
) PARTITION BY RANGE (period);

-- Une partition par année de périodes, sans partition par défaut (ADR 0007).
-- Toute nouvelle partition doit aussi recevoir le trigger anti-TRUNCATE (voir 0002).
DO $$
DECLARE
    partition_year INTEGER;
BEGIN
    FOR partition_year IN 2020..2039 LOOP
        EXECUTE format(
            'CREATE TABLE ledger.%I PARTITION OF ledger.events FOR VALUES FROM (%L) TO (%L)',
            'events_' || partition_year,
            partition_year || '-01',
            (partition_year + 1) || '-01');
    END LOOP;
END
$$;

-- Vérification du sceau de période (trigger) et requêtes par entité et période.
CREATE INDEX events_entity_period_type ON ledger.events (entity_id, period, event_type);

-- Unicité globale (concurrence optimiste) et accès direct à un flux sans parcourir les partitions.
CREATE TABLE ledger.stream_versions (
    stream_id       TEXT           NOT NULL,
    stream_version  BIGINT         NOT NULL,
    event_id        UUID           NOT NULL,
    global_sequence BIGINT         NOT NULL,
    period          TEXT COLLATE "C" NOT NULL,
    hash            TEXT           NOT NULL,
    CONSTRAINT stream_versions_pkey PRIMARY KEY (stream_id, stream_version),
    CONSTRAINT stream_versions_event_id_key UNIQUE (event_id),
    CONSTRAINT stream_versions_global_sequence_key UNIQUE (global_sequence),
    CONSTRAINT stream_versions_event_fkey FOREIGN KEY (global_sequence, period) REFERENCES ledger.events (global_sequence, period)
);

-- Réservation des identifiants d'écriture dans tout le journal (ADR 0003).
CREATE TABLE ledger.entry_ids (
    entry_id UUID NOT NULL,
    event_id UUID NOT NULL,
    CONSTRAINT entry_ids_pkey PRIMARY KEY (entry_id),
    CONSTRAINT entry_ids_event_fkey FOREIGN KEY (event_id) REFERENCES ledger.stream_versions (event_id)
);
