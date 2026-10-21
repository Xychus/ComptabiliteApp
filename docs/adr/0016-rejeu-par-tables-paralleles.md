# 0016 — Rejeu d'une projection par tables parallèles et bascule (blue/green)

- **Statut** : proposé
- **Date** : 2026-10-20
- **Phase** : 4

## Contexte

Toute projection doit pouvoir être vidée et reconstruite à partir de `ledger.events`
(invariant 2) : correction d'un bogue de projection, nouvelle projection, changement de la
configuration fiscale (ADR 0018). La reconstruction ne doit ni interrompre l'API de requête, ni
arrêter le consommateur Kafka, ni perdre ou doubler un événement validé pendant le rejeu.

Un curseur sur `global_sequence` ne suffit pas à lui seul : un événement de séquence inférieure
au curseur peut être validé après sa lecture (ADR 0007, point 6).

## Décision

L'outil est un mode du worker : `Ledger.Projections.Worker rebuild <projection>` (tâche
ponctuelle, même image, même rôle `ledger_projection`). `ProjectionRebuilder` (Application) :

1. **Préparation** : crée les tables parallèles vides `{table}__rebuild` pour chaque table de la
   projection, y compris `{projection}_applied`, par `CREATE TABLE … (LIKE … INCLUDING ALL)`
   (mêmes colonnes, contraintes et index). Une reconstruction inachevée précédente est abandonnée.
2. **Rejeu** : lecture du journal par lots (1 000 par défaut), curseur sur `global_sequence`,
   une transaction par lot, écrite dans les tables parallèles par les **mêmes classes de
   projection** que le service (session ouverte sur `ProjectionTarget.Rebuild`). Le consommateur
   en service continue d'alimenter les tables en service.
3. **Rattrapage et bascule, dans une seule transaction** :
   - verrou consultatif **exclusif** de la projection (`pg_advisory_xact_lock`) ; le consommateur
     prend le même verrou en **partagé** avant de toucher une projection, il attend donc la fin
     de la bascule ;
   - application de tous les événements des types traités par la projection qui sont **absents
     de `{projection}_applied__rebuild`** (anti-jointure sur `event_id`) : ceux validés pendant
     le rejeu et ceux validés hors ordre de séquence ;
   - renommage : `{table}` → `{table}__retired`, `{table}__rebuild` → `{table}`, suppression de
     l'ancienne table, `GRANT SELECT` à `ledger_query` ; la position de reconstruction remplace
     celle en service.
4. Après la bascule, le consommateur reprend sur les nouvelles tables. Les événements qu'il
   relivre et que la reconstruction a déjà appliqués sont écartés par l'idempotence par
   `event_id` (ADR 0015).

Le rôle `ledger_projection` possède les tables du schéma `projections` (renommage et
suppression exigent la propriété) et y a `CREATE`. Il n'a que `SELECT` sur le journal.

## Conséquences

- Les lectures de l'API de requête voient l'ancienne ou la nouvelle table, jamais un état
  intermédiaire ; le renommage prend un verrou `ACCESS EXCLUSIVE` bref sur chaque table.
- L'anti-jointure finale parcourt tout le journal des types concernés : coût proportionnel au
  journal, sous verrou. Acceptable pour une opération d'exploitation ; à mesurer en phase 11
  (sinon : rattrapage préalable hors verrou, puis anti-jointure restreinte aux séquences récentes).
- Les index des tables reconstruites gardent un nom dérivé de `__rebuild` : sans effet fonctionnel.
- Le test d'acceptation « vider puis rejouer donne les mêmes soldes » est exécuté pour chaque
  projection sur les deux implémentations (mémoire et PostgreSQL), et en propriété (FsCheck)
  sur des journaux générés avec relivraisons.

## Options écartées

- **`TRUNCATE` puis rejeu sur place** : l'API servirait des soldes faux pendant le rejeu.
- **Schéma entier en double, échangé par renommage de schéma** : reconstruit toutes les
  projections à la fois.
- **Vues pointant vers la génération active** : même besoin de DDL, une indirection de plus.
- **Arrêt du consommateur pendant le rejeu** : interruption de service inutile.
