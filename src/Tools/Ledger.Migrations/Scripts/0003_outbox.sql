-- Outbox lue par Debezium (réplication logique) : une ligne par événement, écrite dans la même
-- transaction que l'événement (ADR 0007). Le code applicatif ne publie jamais dans Kafka.
-- payload = enveloppe EventEnvelopeV1 complète. Purge après publication : phase 5.
CREATE TABLE ledger.outbox (
    event_id      UUID        NOT NULL,
    topic         TEXT        NOT NULL,
    partition_key TEXT        NOT NULL,
    payload       JSONB       NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT outbox_pkey PRIMARY KEY (event_id)
);
