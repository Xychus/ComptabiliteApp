-- Base de Keycloak (ADR 0030) : schéma dédié dans la base du grand livre. Le rôle keycloak n'a
-- d'usage que sur ce schéma (ni ledger ni projections, dont PUBLIC est exclu) et y crée ses tables
-- (Liquibase de Keycloak), qui lui appartiennent. Le schéma appartient au rôle des migrations :
-- CREATE SCHEMA … AUTHORIZATION exigerait de pouvoir endosser keycloak (PostgreSQL 16+).
-- Le rôle reste NOLOGIN : en AWS, la Lambda de rotation lui donne LOGIN et un mot de passe
-- renouvelé tous les 5 jours (pilote JDBC sans authentification IAM, ADR 0029) ; en local, rien.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'keycloak') THEN
        CREATE ROLE keycloak NOLOGIN;
    END IF;
END
$$;

CREATE SCHEMA IF NOT EXISTS keycloak;
REVOKE ALL ON SCHEMA keycloak FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA keycloak TO keycloak;
