# 0029 — Connexion à Aurora : authentification IAM, mot de passe renouvelé pour Debezium

- **Statut** : accepté
- **Date** : 2026-10-25
- **Phase** : 8

## Contexte

Les rôles applicatifs (`ledger_app`, `ledger_projection`, `ledger_query`, `ledger_maintenance`,
`debezium`) sont créés `NOLOGIN` par les migrations (ADR 0007). La phase 7 a laissé ouvert le mode
de connexion en AWS : authentification IAM (activée sur la grappe) ou mots de passe renouvelés
tous les 7 jours (`CLAUDE.md`). La journalisation des DDL est active sur Aurora (phase 7).

## Décision

1. **Services .NET et purge planifiée : authentification IAM.** Le mot de passe est un jeton
   signé par le rôle IAM de la tâche (valable 15 minutes), produit par
   `Ledger.Postgres.Infrastructure` (`UsePeriodicPasswordProvider`, renouvelé toutes les 10
   minutes), avec `SSL Mode=VerifyFull` obligatoire. Rien n'est stocké ni à renouveler. Le rôle
   de tâche n'a que `rds-db:connect` sur son utilisateur. Migration `0009` : `rds_iam` et `LOGIN`
   pour les quatre rôles, seulement si `rds_iam` existe (Aurora) ; en local, rien ne change.
2. **Debezium : mot de passe renouvelé tous les 5 jours** par la Lambda de rotation (ADR 0010,
   type de secret `mot-de-passe-postgres`) : son pilote JDBC ne sait pas produire de jeton IAM.
   Mot de passe aléatoire de 40 caractères ; `ALTER ROLE … LOGIN PASSWORD` avec un **vérificateur
   SCRAM-SHA-256 calculé côté client** (jamais le mot de passe en clair dans les journaux DDL),
   nom du rôle cité par le serveur ; test de connexion avant promotion ; Debezium redéployé. La
   Lambda se connecte avec le rôle maître, dont le secret est géré et renouvelé par RDS.
3. Debezium lit le mot de passe par le fournisseur de configuration `env` (secret ECS), comme en
   local ; la connexion est `verify-full` contre le paquet régional de certificats racines RDS
   (`infra/rds/`).

## Conséquences

- Aucun mot de passe applicatif hors de Debezium ; la rotation de 7 jours est tenue sans redémarrer
  les services .NET.
- Limite de connexions IAM par seconde (de l'ordre de 200) : sans effet avec les pools Npgsql.
- Entre `setSecret` et la fin de la rotation, une reconnexion de Debezium échoue quelques
  secondes ; Debezium réessaie et il est redéployé avec le nouveau mot de passe.
- La Lambda de la PKI accède à Aurora (5432) et au secret maître : surface à surveiller.

## Options écartées

- **Mots de passe pour tous les rôles** : rotation et redémarrage de chaque service tous les
  7 jours, secrets à distribuer.
- **Wrapper JDBC AWS pour l'IAM de Debezium** : bibliothèque à ajouter à l'image officielle.
- **Modèle de rotation PostgreSQL d'AWS (SAR)** : ses variables d'environnement Lambda sont
  chiffrées par `aws/lambda` (clé gérée par AWS).
- **Debezium avec le rôle maître** : privilèges excessifs.
