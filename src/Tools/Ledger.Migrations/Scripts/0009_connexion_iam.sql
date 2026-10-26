-- Connexion des rôles applicatifs à Aurora par authentification IAM (ADR 0029) : aucun mot de
-- passe stocké ni à renouveler ; le jeton est signé par le rôle IAM de la tâche ECS et valable
-- 15 minutes. Le rôle rds_iam n'existe que sur RDS/Aurora : en local, les rôles restent NOLOGIN
-- et deploy/local/roles-locaux.sh leur donne un mot de passe.
-- Debezium n'est pas concerné : son pilote JDBC ne sait pas produire de jeton IAM ; la Lambda de
-- rotation lui donne LOGIN et un mot de passe renouvelé tous les 5 jours.
DO $$
BEGIN
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'rds_iam') THEN
        GRANT rds_iam TO ledger_app, ledger_projection, ledger_query, ledger_maintenance;
        ALTER ROLE ledger_app LOGIN;
        ALTER ROLE ledger_projection LOGIN;
        ALTER ROLE ledger_query LOGIN;
        ALTER ROLE ledger_maintenance LOGIN;
    END IF;
END
$$;
