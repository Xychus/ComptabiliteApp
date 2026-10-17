# 0002 — Architecture hexagonale par service, règles vérifiées en CI

- **Statut** : accepté
- **Date** : 2026-10-16
- **Phase** : 0

## Contexte

Les invariants comptables (partie double, solde non négatif, période scellée) doivent être testés
exhaustivement sans base de données ni réseau, et rester indépendants des choix d'infrastructure
(PostgreSQL, Kafka, AWS). L'inversion des dépendances (SOLID) n'a de valeur que si elle est
vérifiée automatiquement : sinon une référence « pratique » finit par la contourner.

## Décision

Chaque service suit une architecture hexagonale (ports et adaptateurs) :

| Couche | Peut dépendre de | Interdit |
|---|---|---|
| `*.Domain` | `Ledger.SharedKernel` | tout paquet NuGet, toute autre couche, toute technologie d'infrastructure |
| `*.Application` (ports) | `Domain`, `Contracts`, `SharedKernel` | `Infrastructure`, `Api`/`Worker`, Npgsql, Kafka, AWS, ASP.NET Core |
| `*.Infrastructure` (adaptateurs) | `Application` et technologies d'infrastructure | `Api`/`Worker` |
| `*.Api` / `*.Worker` (composition) | tout ce qui précède | — (rien ne dépend d'eux) |

S'y ajoutent : `Ledger.SharedKernel` ne dépend d'aucun autre projet ; `Ledger.Contracts` ne dépend
d'aucune couche de service ; les services (`Command`, `Projections`, `Query`) ne dépendent pas les
uns des autres.

Ces règles sont vérifiées par `tests/Architecture/Ledger.Architecture.Tests`, à deux niveaux :

1. **Dépendances entre types** avec ArchUnitNET : détecte l'usage effectif d'un type interdit,
   y compris par une dépendance transitive.
2. **Références déclarées** par analyse des `.csproj` : une `PackageReference` encore inutilisée
   n'apparaît pas dans les métadonnées compilées (le compilateur l'élimine) et échapperait à
   ArchUnitNET. Un `*.Domain` ne doit avoir aucune `PackageReference` et ne référencer que
   `Ledger.SharedKernel`.

Chaque projet de `src/` contient un type `AssemblyMarker` pour charger son assembly et éviter
qu'une règle soit évaluée sur un ensemble vide (et passe à tort).

## Conséquences

- Ajouter `Npgsql` à `Ledger.Command.Domain` fait échouer le CI (vérifié en phase 0 : la référence
  seule échoue au niveau 2, son usage échoue en plus au niveau 1).
- Un nouveau service ou une nouvelle couche doit être ajouté au chargement de
  `LedgerArchitecture` et, si nécessaire, aux règles.
- Les règles ne remplacent pas la revue : elles bornent les dépendances, pas la qualité du découpage.

## Options écartées

- **NetArchTest** : fonctionnel, mais ArchUnitNET offre une API plus riche (règles combinées,
  messages d'échec détaillés) et une intégration xUnit v3 dédiée.
- **Analyse uniquement des `.csproj`** : ne voit pas un usage transitif (ex. un type
  d'infrastructure exposé par un projet autorisé).
- **Une seule assembly par service avec des dossiers** : les dépendances ne seraient vérifiables
  qu'au niveau des espaces de noms, sans garde-fou du compilateur.
