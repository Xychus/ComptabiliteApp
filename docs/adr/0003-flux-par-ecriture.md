# 0003 — Un flux par écriture, identifiant d'écriture réservé dans tout le journal

- **Statut** : proposé
- **Date** : 2026-10-16
- **Phase** : 2

## Contexte

Le client choisit l'identifiant d'une écriture (`entryId`, UUIDv7) et l'envoie par un `PUT`
idempotent (phase 3). Rejouer la même requête ne doit jamais créer de doublon ; réutiliser un
identifiant pour un contenu différent doit être refusé. La phase 1 a stocké la contre-passation
dans le flux de l'écriture d'origine (une seconde contre-passation entre en conflit), mais
l'identifiant de l'écriture inverse (`reversalEntryId`) n'a pas de flux propre : sans garde-fou,
un `PUT` ultérieur pourrait le réutiliser (Journal, phase 1, point 2).

Une contrainte d'unicité sur un contenu métier (ex. index unique sur `payload->>'entryId'`) n'est
pas possible sur la table `events` partitionnée (ADR 0007) : PostgreSQL exige que toute contrainte
unique d'une table partitionnée contienne la clé de partition.

## Décision

1. Chaque écriture a son propre flux `entry-{entryId}`. `JournalEntryPosted` y est toujours en
   `stream_version = 1` ; `JournalEntryReversed` en `stream_version = 2`.
2. L'unicité de `(stream_id, stream_version)` est la clé d'idempotence : deux `PUT` concurrents du
   même `entryId` se disputent la version 1, un seul l'obtient.
3. Un identifiant d'écriture est **réservé dans tout le journal** : chaque événement qui crée une
   écriture déclare l'identifiant qu'il revendique (`ClaimedEntryId` : `entryId` pour
   `JournalEntryPosted`, `reversalEntryId` pour `JournalEntryReversed`). L'event store garantit
   qu'un identifiant n'est revendiqué qu'une fois (table append-only `ledger.entry_ids` en
   PostgreSQL). Une seconde revendication est refusée par `EntryIdAlreadyUsed`, sans nouvelle
   tentative.
4. Comportement de l'Application sur un `entryId` déjà présent dans son flux :
   - charge identique (même entité, date, description et lignes) → l'écriture existante est
     renvoyée, rien n'est écrit ;
   - charge différente → rejet `EntryIdConflict`.
5. Un conflit de version (`ConcurrencyConflict`) déclenche une nouvelle lecture et une nouvelle
   décision, dans une limite de tentatives (5 par défaut). Au-delà, le conflit est renvoyé.

## Conséquences

- L'idempotence ne dépend d'aucun état en mémoire ni d'une table de clés d'idempotence à purger :
  elle découle du journal.
- Passer une écriture lit un flux d'au plus deux événements : coût constant.
- La table `entry_ids` est dérivée du journal (reconstructible par rejeu) mais écrite dans la même
  transaction que les événements ; elle est soumise aux mêmes règles append-only.
- La comparaison de charge se fait sur les événements de domaine reconstruits : deux montants
  égaux d'échelles différentes (`5` et `5.00`) sont la même charge.

## Options écartées

- **Flux par entité ou par période** : sérialiserait toutes les écritures d'une entité.
- **Table de clés d'idempotence avec réponse mémorisée** : état supplémentaire, durée de rétention
  à gérer, et la réponse mémorisée pourrait diverger du journal.
- **Flux propre pour l'écriture inverse** : une seconde contre-passation ne serait plus un conflit
  de version dans le flux d'origine (décision de la phase 1).
