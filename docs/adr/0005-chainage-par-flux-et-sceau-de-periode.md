# 0005 — Chaînage par hash par flux et sceau de période

- **Statut** : proposé
- **Date** : 2026-10-16
- **Phase** : 2

## Contexte

Revenu Québec exige une conservation de 6 ans avec une intégrité vérifiable : il faut pouvoir
prouver qu'aucun événement n'a été modifié, supprimé ou inséré après coup. Une chaîne de hash
unique pour tout le journal imposerait que chaque écriture connaisse le hash de la précédente,
donc sérialiserait toutes les écritures du système.

## Décision

1. **Chaînage par flux** : chaque événement porte `hash` et `previous_hash`. `previous_hash` est
   le hash de la version précédente du **même flux** ; il est `NULL` en version 1 (contrainte
   `CHECK` en base). Le hash est calculé par l'event store (autorité serveur), selon l'ADR 0006.
2. Le hash couvre tous les champs de l'événement sauf `global_sequence` (attribué par la base à
   l'insertion) et le hash lui-même ; il couvre donc `recorded_at`, la période et `previous_hash`.
3. **Sceau de période** : sceller une période (`PeriodSealed`) fige l'ensemble de ses événements.
   Le sceau publiera la racine de Merkle des hash de tous les événements de l'entité dans la
   période, ordonnés par `global_sequence` (feuilles = hash des événements, nœud =
   SHA-256(gauche ‖ droite), dernier nœud impair promu tel quel). Le calcul de la racine est
   implémenté avec la commande de scellement (phase 3) et vérifié par `Ledger.Integrity` (phase 11).
4. **Le journal refuse toute écriture dans une période scellée**, y compris en cas de course
   avec le scellement. En PostgreSQL, un trigger `BEFORE INSERT` sur `events` :
   - prend un verrou consultatif transactionnel sur (entité, période) : **exclusif** pour
     `PeriodSealed`, **partagé** pour tout autre événement ;
   - refuse l'insertion (SQLSTATE `LG002`) si un `PeriodSealed` existe déjà pour (entité, période).

   Les écritures d'une même période ne se bloquent donc pas entre elles ; le scellement attend
   les transactions en cours et toute écriture postérieure voit le sceau. Les transactions de
   l'event store sont en `READ COMMITTED` (chaque instruction du trigger voit les validations
   les plus récentes).

## Conséquences

- Les écritures de flux différents restent parallèles.
- Une suppression ou modification se détecte par la rupture de chaîne de son flux ; une
  suppression du dernier événement d'un flux ou l'insertion d'un flux entier se détectent par la
  racine de Merkle de la période scellée.
- Tant qu'une période n'est pas scellée, seule la chaîne par flux la protège : sceller les
  périodes rapidement après leur clôture fait partie de l'exploitation (phase 11).
- L'event store connaît le nom de type `PeriodSealed` (trigger et implémentation en mémoire) :
  ce nom est un contrat stable (`EventTypes.PeriodSealed`).

## Options écartées

- **Chaîne globale unique** : sérialise toutes les écritures.
- **Chaîne par entité** : sérialise toutes les écritures d'une entité.
- **Vérification du sceau uniquement dans l'Application** : une écriture décidée avant le
  scellement pourrait être validée après lui (course entre lecture du flux de période et
  insertion).
