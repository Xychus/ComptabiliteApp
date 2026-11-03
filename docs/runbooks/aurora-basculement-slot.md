# Aurora : basculement et slot logique de Debezium

Point P4 du plan (ADR 0025) : la documentation AWS ne dit pas si un slot de réplication logique
survit au basculement d'Aurora PostgreSQL. **À constater au point d'arrêt de la phase 8.**

## Essai en dev (Debezium actif)

1. Vérifier l'état : connecteur `RUNNING`, alarme `OldestReplicationSlotLag` au repos.
2. Écrire quelques écritures (API de commandes, ou script de démonstration de la phase 5).
3. Forcer le basculement :
   ```bash
   aws rds failover-db-cluster --db-cluster-identifier ledger-dev
   ```
   En dev, le lecteur Serverless peut être en pause (ADR 0027) : la durée n'est pas
   représentative de la prod.
4. Pendant et après : écrire de nouveau ; observer les journaux de Kafka Connect
   (`/ledger-dev/debezium`), la métrique `Ledger/Cdc EchecsConnecteur`, le slot sur le nouveau
   rédacteur :
   ```sql
   SELECT slot_name, active, confirmed_flush_lsn FROM pg_replication_slots;
   ```
5. Vérifier qu'aucun événement n'est perdu ni dupliqué dans les projections (sommes de contrôle
   de la balance, `GET /v1/balance`).

## Résultat attendu et repli

- **Le slot survit** : Debezium se reconnecte au point de terminaison rédacteur et reprend.
- **Le slot disparaît** : le connecteur échoue (alarme). Recréer le slot n'est pas suffisant (le
  WAL intermédiaire est perdu) : supprimer le connecteur, laisser Debezium refaire un snapshot
  `initial` de l'outbox (la purge garde 7 jours de lignes), les projections ignorent les doublons
  (idempotence par `event_id`, ADR 0015). Si l'outbox a été purgée au-delà du trou, rejouer les
  projections depuis le journal (ADR 0016).

## Constat (à compléter)

| Date | Environnement | Durée du basculement | Slot conservé | Événements perdus | Notes |
|---|---|---|---|---|---|
| | dev | | | | |
