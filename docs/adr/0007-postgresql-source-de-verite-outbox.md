# 0007 — PostgreSQL est la source de vérité ; outbox + Debezium ; schéma du journal

- **Statut** : proposé
- **Date** : 2026-10-16
- **Phase** : 2

## Contexte

Les projections (phase 4) sont alimentées par Kafka. Écrire dans PostgreSQL puis publier dans
Kafka depuis le code applicatif est une double écriture : un échec entre les deux perd ou
duplique un événement. Le journal doit en outre rester append-only, même face au rôle applicatif,
et supporter six ans d'historique.

## Décision

### Source de vérité et publication

1. La table `ledger.events` est la seule source de vérité ; Kafka n'est qu'un transport.
2. Chaque événement est accompagné, **dans la même transaction**, d'une ligne de
   `ledger.outbox` (`event_id`, `topic`, `partition_key`, `payload`). Debezium lit l'outbox par
   réplication logique (publication `ledger_pub`) et publie dans Kafka. Le code applicatif ne
   publie jamais.
3. Le `payload` de l'outbox est l'enveloppe complète (`EventEnvelopeV1` : métadonnées,
   `global_sequence`, hash et charge de l'événement). Sujet : `ledger.events` ; clé de partition :
   l'entité, ce qui conserve l'ordre de validation des événements d'une entité.

### Schéma

4. Tout est dans le schéma `ledger`. `events` est **partitionnée par plage de périodes**
   (`period`, `AAAA-MM`) avec **une partition par année** (`events_2026` = `2026-01` à `2026-12`),
   créées de 2020 à 2039 par migration, sans partition par défaut : une écriture hors de ces
   années échoue bruyamment plutôt que de se perdre dans une partition fourre-tout. Une migration
   ajoutera les années suivantes.
5. PostgreSQL impose la clé de partition dans toute contrainte d'unicité d'une table partitionnée.
   L'unicité globale est donc portée par des tables compagnes non partitionnées, écrites dans la
   même transaction et elles aussi append-only :
   - `stream_versions` : clé primaire `(stream_id, stream_version)` (concurrence optimiste),
     `event_id` unique, et `global_sequence`, `period`, `hash` pour lire un flux et chaîner sans
     parcourir toutes les partitions ;
   - `entry_ids` : réservation des identifiants d'écriture (ADR 0003).

   La clé primaire de `events` est `(global_sequence, period)`.
6. `global_sequence` est attribué par une séquence, imposé par un trigger `BEFORE INSERT` (une
   valeur fournie en entrée est écrasée). Il croît dans l'ordre d'insertion, pas forcément dans
   l'ordre de validation : un lecteur du journal par curseur doit en tenir compte (le rejeu
   complet n'est pas concerné ; la publication passe par le WAL, dans l'ordre de validation).
7. **Append-only** : un trigger refuse `UPDATE` et `DELETE` (par ligne) et `TRUNCATE` (sur la
   table et chaque partition) sur `events`, `stream_versions` et `entry_ids`, quel que soit le
   rôle (SQLSTATE `LG001`). En plus, les rôles applicatifs n'ont que `SELECT`/`INSERT`.
8. Rôles (sans connexion à la création ; la connexion et les secrets sont gérés hors des
   migrations, par Secrets Manager en phase 7) :
   - `ledger_app` : `SELECT`, `INSERT` sur le journal, ses tables compagnes et l'outbox ;
     `SELECT` sur `accounts` et `periods` ;
   - `ledger_projection` : `SELECT` sur le journal (rejeu) ;
   - `debezium` : `REPLICATION` (ou `rds_replication` sur Aurora), `SELECT` sur l'outbox.
9. Tables de référence : `accounts` (plan comptable par entité, dont `no_negative_balance`) et
   `periods` (calendrier fiscal par entité). Le **statut** d'une période (ouverte, scellée) n'est
   pas dans `periods` : il vient de son flux d'événements (`period-{entité}-{AAAA-MM}`).
10. Migrations : DbUp, scripts SQL numérotés intégrés à `Ledger.Migrations`, exécutés par une
    tâche ponctuelle avec le rôle propriétaire. Un script fusionné n'est jamais modifié.

## Conséquences

- Aucune perte ni duplication entre PostgreSQL et Kafka : ce qui est validé est publié, au moins
  une fois (consommateurs idempotents, invariant 7).
- Les écritures écrivent quatre tables au lieu d'une ; la lecture d'un flux passe par
  `stream_versions` puis `events`, avec élagage des partitions à l'exécution.
- L'outbox grossit indéfiniment tant qu'aucune purge n'est définie (rôle dédié, après
  publication) : à traiter en phase 5.
- La partition par année devra suivre l'horizon des dates d'affaires (alarme en phase 11).
- Le trigger de période scellée (ADR 0005) et celui d'append-only sont des garanties en base,
  indépendantes du code applicatif.

## Options écartées

- **Publication directe dans Kafka après validation** : double écriture.
- **Kafka comme source de vérité** : pas de transaction avec la vérification de concurrence,
  rétention et audit plus complexes.
- **Une partition par mois** : 12 fois plus de partitions (et d'index) pour un gain nul sur le
  chemin d'écriture ; l'archivage et le scellement se raisonnent aussi bien par année.
- **Partition par défaut** : masquerait une date d'affaires hors plage.
- **Unicité `(stream_id, stream_version, period)` sur `events`** : ne garantit rien, une même
  version pouvant exister dans deux périodes.
