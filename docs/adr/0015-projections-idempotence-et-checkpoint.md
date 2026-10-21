# 0015 — Projections : consommation, idempotence et checkpoint transactionnel

- **Statut** : proposé
- **Date** : 2026-10-20
- **Phase** : 4

## Contexte

Les modèles de lecture (soldes, grand livre, plan comptable, périodes, instantanés, TPS/TVQ) sont
alimentés par Kafka, où Debezium publiera l'outbox (phase 5, ADR 0007). La livraison Kafka est
« au moins une fois » : un message peut être relivré après un redémarrage, et Debezium peut
republier une ligne d'outbox à un nouvel offset après une reprise. Les projections doivent donc
être idempotentes (invariant 7) et ne jamais perdre un événement.

Deux pièges propres à ce journal :

- `global_sequence` croît dans l'ordre d'**insertion**, pas de validation (ADR 0007, point 6) :
  un événement de séquence 10 peut être validé, donc publié, après celui de séquence 11. Un
  checkpoint « plus grande séquence appliquée » ignorerait à tort l'événement 10.
- Un offset Kafka n'identifie pas un événement : le même événement republié a un autre offset.

## Décision

1. **Schéma `projections`**, dans la même base que le journal, propriété du rôle
   `ledger_projection` (migration 0007). Le rôle `ledger_query` (API de requête) n'y a que
   `SELECT`, et `SELECT` sur `ledger.events` (fraîcheur). `ledger_app` n'y a aucun droit.
   La table de référence `ledger.accounts` est supprimée ; sa forme lisible est la projection
   `chart_of_accounts` (ADR 0012, point 6).
2. **Une transaction par message** (`IProjectionSession`, `READ COMMITTED`) qui écrit, ensemble :
   - les modèles de lecture de chaque projection concernée ;
   - pour chaque projection, l'identifiant de l'événement dans sa table `{projection}_applied`
     (`event_id` clé primaire, `INSERT … ON CONFLICT DO NOTHING`) ; une projection ne traite
     l'événement que si l'insertion a eu lieu : **idempotence par `event_id`**, indépendante de
     l'ordre de séquence et de l'offset ;
   - la position de chaque projection (`projection_positions` : plus grande séquence vue, pour
     l'indicateur de fraîcheur ; elle ne recule jamais) ;
   - le **checkpoint du consommateur** (`consumer_offsets` : groupe, sujet, partition, prochain
     offset).
   Soit tout est validé, soit rien : un arrêt brutal entre deux messages ne perd ni ne double
   aucun effet.
3. **Reprise depuis la base, pas depuis Kafka.** À l'affectation d'une partition, le
   consommateur (Confluent.Kafka) se positionne sur l'offset enregistré en base, ou au début de
   la partition s'il n'y en a pas. Les offsets sont aussi stockés dans Kafka après traitement
   (`EnableAutoOffsetStore = false`, `StoreOffset` puis validation automatique), uniquement pour
   mesurer le retard du groupe.
4. **Traitement par partition, dans l'ordre** : un message à la fois. La clé de partition est
   l'entité (ADR 0007) : les événements d'une entité arrivent dans leur ordre de validation, et
   une contre-passation arrive toujours après l'écriture qu'elle annule (même flux, ADR 0003).
5. **Chaque projection est une classe** (`IProjection` + un `IEventHandler<T>` par type
   d'événement traité) qui n'écrit que par son propre port (`IAccountBalanceStore`,
   `ILedgerEntryStore`…). Ajouter une projection = ajouter une classe, un port, ses tables et
   ses deux implémentations ; le moteur ne change pas. Les `AccountLinePosted` (comptes
   contraints) ne sont pas comptés : ils répètent des lignes d'écriture (ADR 0004).
6. **Upcasting à la lecture** : `EventDecoder` convertit la charge de sa version de schéma vers
   la version courante du contrat par une chaîne d'`IEventUpcaster` (`n → n+1`), puis la
   désérialise. Aujourd'hui tous les événements sont en V1 : la chaîne est vide, le mécanisme est
   testé avec un type d'essai sur trois versions.
7. **Erreur = arrêt.** Un type inconnu, une version sans upcaster ou plus récente que le worker,
   une charge illisible ou une incohérence (contre-passation d'une écriture absente) lève une
   exception : la transaction est annulée, la boucle s'arrête, l'hôte s'arrête et
   l'orchestrateur le relance (reprise au dernier checkpoint). Ignorer un message fausserait les
   soldes sans bruit. Déployer d'abord le worker qui connaît une nouvelle version de schéma.
8. Aucune colonne de projection ne prend l'heure du worker : tout vient de l'événement
   (`recorded_at`, séquence, version de flux). C'est ce qui rend le rejeu exact (ADR 0016).

## Conséquences

- Les tables `{projection}_applied` grossissent d'une ligne par événement traité et par
  projection. C'est le prix d'une idempotence exacte malgré la séquence non monotone. Une purge
  sûre (événements des périodes scellées) pourra être ajoutée si le volume l'exige (phase 11).
- Un message empoisonné bloque sa partition jusqu'à correction (alarme sur le retard du groupe,
  phase 11). C'est voulu pour un grand livre.
- Le débit est celui d'un message par transaction ; regrouper plusieurs messages d'une partition
  par transaction est une optimisation possible sans changer les garanties.
- Le format exact du message produit par l'*outbox event router* de Debezium (valeur = enveloppe
  JSON `EventEnvelopeV1`, clé = entité) est vérifié de bout en bout en phase 5.

## Options écartées

- **Checkpoint « dernière séquence appliquée »** : faux avec une séquence attribuée à l'insertion.
- **Offsets validés dans Kafka seulement** : la validation Kafka et la transaction PostgreSQL ne
  sont pas atomiques (perte ou double application après un arrêt).
- **Transactions Kafka (exactly-once)** : ne couvrent pas l'écriture dans PostgreSQL.
- **Une table d'événements appliqués commune à toutes les projections** : incompatible avec la
  reconstruction d'une seule projection (ADR 0016).
