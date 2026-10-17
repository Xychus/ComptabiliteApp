-- Sceau de période vérifié contre le nombre d'événements scellés (ADR 0013).
-- Remplace le corps de ledger.guard_event_insert (0002) ; le trigger events_guard_insert reste en place.
-- À l'insertion d'un événement :
--  1. global_sequence vient toujours de la séquence (une valeur fournie est écrasée) ;
--  2. verrou consultatif sur (entité, période) : exclusif pour PeriodSealed, partagé sinon ;
--  3. refus si la période est déjà scellée (SQLSTATE LG002) ;
--  4. pour PeriodSealed, sous le verrou exclusif : refus si le nombre d'événements de (entité,
--     période) diffère de payload->>'eventCount', absent compris (SQLSTATE LG003). L'Application
--     recalcule alors l'empreinte et recommence.
CREATE OR REPLACE FUNCTION ledger.guard_event_insert() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    seal_key BIGINT := hashtextextended(NEW.entity_id || '|' || NEW.period, 0);
    sealed_count BIGINT;
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

    IF NEW.event_type = 'PeriodSealed' THEN
        SELECT count(*) INTO sealed_count
        FROM ledger.events
        WHERE entity_id = NEW.entity_id AND period = NEW.period;

        IF jsonb_typeof(NEW.payload -> 'eventCount') IS DISTINCT FROM 'number'
            OR (NEW.payload ->> 'eventCount')::NUMERIC IS DISTINCT FROM sealed_count THEN
            RAISE EXCEPTION 'Le sceau de la période % de l''entité % couvre % événement(s), la période en compte %.',
                NEW.period, NEW.entity_id, NEW.payload ->> 'eventCount', sealed_count
                USING ERRCODE = 'LG003';
        END IF;
    END IF;

    RETURN NEW;
END
$$;
