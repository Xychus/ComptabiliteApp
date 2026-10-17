# 0013 — Contenu du sceau de période : nombre d'événements et racine de Merkle

- **Statut** : proposé
- **Date** : 2026-10-16
- **Phase** : 3

## Contexte

L'ADR 0005 prévoit que `PeriodSealed` publie la racine de Merkle des hash de tous les événements
de l'entité dans la période, ordonnés par `global_sequence`, et laisse son calcul à la commande
de scellement. Or la racine est calculée avant l'insertion du sceau : une écriture validée entre
la lecture des hash et la prise du verrou exclusif du trigger serait couverte par le sceau sans
faire partie de la racine.

## Décision

1. `PeriodSealed` porte `eventCount` (nombre d'événements de l'entité dans la période, hors le
   sceau lui-même) et `merkleRoot` (hexadécimal minuscule, 64 caractères).
2. Racine de Merkle (`Ledger.Contracts.Integrity.MerkleTree`), conforme à l'ADR 0005 :
   feuilles = hash des événements (32 octets) dans l'ordre de `global_sequence` ; nœud =
   SHA-256(gauche ‖ droite) ; un nœud impair en fin de niveau est promu tel quel ; une liste vide
   a pour racine SHA-256 de la chaîne vide (cas théorique : une période ouverte contient au moins
   `PeriodOpened`).
3. L'Application lit les hash par un port dédié (`IPeriodEventReader`), calcule la racine et le
   nombre, et les passe au décideur dans la commande `SealPeriod`.
4. **L'event store vérifie le nombre au moment du sceau.** En PostgreSQL, le trigger
   `guard_event_insert` (migration 0006, qui remplace la fonction de la migration 0002) compte,
   **sous le verrou consultatif exclusif**, les événements de (entité, période) et refuse le sceau
   (SQLSTATE `LG003`) si ce nombre diffère de `payload->>'eventCount'`, absent compris.
   L'implémentation en mémoire fait le même contrôle. Le refus est un `ConcurrencyConflict` :
   l'Application relit les hash et recommence, dans la limite de la politique de tentatives.
5. Ce contrôle suffit : la table est append-only, donc le nombre ne fait que croître ; sous le
   verrou exclusif, toutes les transactions qui ont inséré dans la période sont validées et toutes
   les suivantes seront refusées par le sceau. Nombre égal ⇒ l'ensemble lu est exactement
   l'ensemble scellé.
6. `PeriodSealedV1` reçoit les deux champs en propriétés `init` : ajout compatible, la V1 reste
   lisible (aucun sceau n'a été écrit hors des tests avant ce changement).

## Conséquences

- `Ledger.Integrity` (phase 11) pourra recalculer la racine à partir du journal et la comparer au
  sceau, et la chaîne de chaque flux garantit le reste.
- L'event store lit un champ de la charge de `PeriodSealed` : comme son nom (ADR 0005), le nom du
  champ `eventCount` devient un contrat stable du stockage.
- Un scellement sur une période très active peut demander plusieurs tentatives ; en pratique on
  scelle une période close, sans écriture concurrente.

## Options écartées

- **Racine calculée par le trigger** (SQL `sha256`) : logique d'intégrité dupliquée en PL/pgSQL et
  dans l'implémentation en mémoire, plus difficile à tester que la vérification d'un nombre.
- **Verrou exclusif pris avant la lecture des hash** : exigerait que l'Application ouvre la
  transaction et prenne un verrou propre à PostgreSQL, hors des ports.
- **Racine sans séparation de domaine (préfixes 0x00 / 0x01 de la RFC 6962)** : conservée telle que
  l'ADR 0005 l'a décidée ; la longueur fixe des feuilles et le nombre d'événements publié dans le
  sceau limitent l'ambiguïté feuille / nœud. À réexaminer à la revue de l'ADR 0005.
