-- Rôles applicatifs (ADR 0007). Créés sans connexion : LOGIN et mots de passe sont attribués
-- hors des migrations (Secrets Manager, phase 7). Aucun secret dans ce script.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ledger_app') THEN
        CREATE ROLE ledger_app NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ledger_projection') THEN
        CREATE ROLE ledger_projection NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'debezium') THEN
        CREATE ROLE debezium NOLOGIN;
    END IF;

    -- Aurora / RDS : l'attribut REPLICATION est réservé au superutilisateur, d'où rds_replication.
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'rds_replication') THEN
        GRANT rds_replication TO debezium;
    ELSE
        ALTER ROLE debezium REPLICATION;
    END IF;
END
$$;

REVOKE ALL ON SCHEMA ledger FROM PUBLIC;
GRANT USAGE ON SCHEMA ledger TO ledger_app, ledger_projection, debezium;

-- Service de commandes : lecture et ajout uniquement ; jamais UPDATE, DELETE ni TRUNCATE.
GRANT SELECT, INSERT ON ledger.events, ledger.stream_versions, ledger.entry_ids, ledger.outbox TO ledger_app;
GRANT SELECT ON ledger.accounts, ledger.periods TO ledger_app;
GRANT USAGE ON SEQUENCE ledger.events_global_sequence TO ledger_app;

-- Projections : lecture du journal pour le rejeu (les tables de projection arrivent en phase 4).
GRANT SELECT ON ledger.events, ledger.stream_versions TO ledger_projection;

-- Debezium : lecture de l'outbox par réplication logique.
GRANT SELECT ON ledger.outbox TO debezium;
CREATE PUBLICATION ledger_pub FOR TABLE ledger.outbox;
