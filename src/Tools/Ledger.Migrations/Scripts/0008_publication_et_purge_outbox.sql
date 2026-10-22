-- Chaîne CDC (ADR 0020). Debezium lit les insertions de l'outbox dans le WAL ; la ligne elle-même
-- ne sert plus après son décodage, sauf au snapshot initial du connecteur.

-- Seules les insertions sont publiées : la purge (DELETE) ne produit aucun message.
ALTER PUBLICATION ledger_pub SET (publish = 'insert');

-- Rôle de la tâche planifiée de purge. Créé sans connexion, comme les autres rôles (ADR 0007).
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ledger_maintenance') THEN
        CREATE ROLE ledger_maintenance NOLOGIN;
    END IF;
END
$$;

-- Purge des lignes plus anciennes que la rétention. Elle exige que le slot de réplication du
-- connecteur existe : tant qu'il existe, le WAL qui contient chaque insertion non encore
-- confirmée est conservé, et l'insertion reste décodable même après la suppression de la ligne.
-- Sans slot (connecteur jamais démarré ou supprimé), le snapshot initial a encore besoin des lignes.
CREATE FUNCTION ledger.purger_outbox(p_retention INTERVAL, p_slot TEXT)
RETURNS BIGINT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    supprimees BIGINT;
BEGIN
    IF p_retention IS NULL OR p_retention < INTERVAL '1 hour' THEN
        RAISE EXCEPTION 'Rétention de purge invalide : % (au moins 1 heure).', p_retention
            USING ERRCODE = 'LG004';
    END IF;

    IF NOT EXISTS (SELECT FROM pg_catalog.pg_replication_slots WHERE slot_name = p_slot AND slot_type = 'logical') THEN
        RAISE EXCEPTION 'Slot de réplication logique % absent : purge refusée.', p_slot
            USING ERRCODE = 'LG005';
    END IF;

    DELETE FROM ledger.outbox WHERE created_at < pg_catalog.now() - p_retention;
    GET DIAGNOSTICS supprimees = ROW_COUNT;
    RETURN supprimees;
END
$$;

REVOKE ALL ON FUNCTION ledger.purger_outbox(INTERVAL, TEXT) FROM PUBLIC;
GRANT USAGE ON SCHEMA ledger TO ledger_maintenance;
GRANT EXECUTE ON FUNCTION ledger.purger_outbox(INTERVAL, TEXT) TO ledger_maintenance;
