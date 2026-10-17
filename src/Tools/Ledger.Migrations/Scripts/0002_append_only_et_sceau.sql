-- Garanties en base, indépendantes du code applicatif et du rôle (ADR 0005 et 0007).

-- Append-only : UPDATE, DELETE et TRUNCATE refusés quel que soit le rôle (SQLSTATE LG001).
CREATE FUNCTION ledger.reject_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'La table %.% est append-only : % refusé.', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'LG001';
END
$$;

CREATE TRIGGER events_append_only BEFORE UPDATE OR DELETE ON ledger.events
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();
CREATE TRIGGER events_no_truncate BEFORE TRUNCATE ON ledger.events
    FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_mutation();

-- Un TRUNCATE visant directement une partition ne déclenche pas le trigger de la table mère.
DO $$
DECLARE
    partition_name REGCLASS;
BEGIN
    FOR partition_name IN SELECT inhrelid::regclass FROM pg_inherits WHERE inhparent = 'ledger.events'::regclass LOOP
        EXECUTE format(
            'CREATE TRIGGER events_no_truncate BEFORE TRUNCATE ON %s FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_mutation()',
            partition_name);
    END LOOP;
END
$$;

CREATE TRIGGER stream_versions_append_only BEFORE UPDATE OR DELETE ON ledger.stream_versions
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();
CREATE TRIGGER stream_versions_no_truncate BEFORE TRUNCATE ON ledger.stream_versions
    FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_mutation();
CREATE TRIGGER entry_ids_append_only BEFORE UPDATE OR DELETE ON ledger.entry_ids
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();
CREATE TRIGGER entry_ids_no_truncate BEFORE TRUNCATE ON ledger.entry_ids
    FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_mutation();

-- À l'insertion d'un événement :
--  1. global_sequence vient toujours de la séquence (une valeur fournie est écrasée) ;
--  2. verrou consultatif sur (entité, période) : exclusif pour PeriodSealed, partagé sinon ;
--  3. refus si la période est déjà scellée (SQLSTATE LG002).
-- Les transactions doivent être en READ COMMITTED : la vérification voit alors un sceau validé
-- pendant l'attente du verrou (ADR 0005).
CREATE FUNCTION ledger.guard_event_insert() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    seal_key BIGINT := hashtextextended(NEW.entity_id || '|' || NEW.period, 0);
BEGIN
    NEW.global_sequence := nextval('ledger.events_global_sequence');

    IF NEW.event_type = 'PeriodSealed' THEN
        PERFORM pg_advisory_xact_lock(seal_key);
    ELSE
        PERFORM pg_advisory_xact_lock_shared(seal_key);
    END IF;

    IF EXISTS (
        SELECT 1 FROM ledger.events
        WHERE entity_id = NEW.entity_id AND period = NEW.period AND event_type = 'PeriodSealed'
    ) THEN
        RAISE EXCEPTION 'La période % de l''entité % est scellée.', NEW.period, NEW.entity_id
            USING ERRCODE = 'LG002';
    END IF;

    RETURN NEW;
END
$$;

CREATE TRIGGER events_guard_insert BEFORE INSERT ON ledger.events
    FOR EACH ROW EXECUTE FUNCTION ledger.guard_event_insert();
