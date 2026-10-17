# 0001 — Monorepo, versions SemVer par composant (release-please, mode manifest)

- **Statut** : accepté
- **Date** : 2026-10-16
- **Phase** : 0

## Contexte

Le grand livre se compose de plusieurs services déployés séparément (commandes, projections,
requêtes, autorizer Lambda), de contrats partagés (HTTP et événements) et de l'infrastructure
Terraform. Un changement de contrat doit pouvoir modifier l'émetteur et ses consommateurs dans un
même commit, tout en laissant chaque service évoluer à son propre rythme de version.

## Décision

1. **Un seul dépôt** (`Ledger.slnx`) pour le code .NET, l'infrastructure et la documentation.
2. **SemVer 2.0.0 par composant** (https://semver.org), calculé par **release-please en mode
   manifest** à partir des Conventional Commits :

   | Composant | Chemin | Tag |
   |---|---|---|
   | `command` | `src/Command` | `command-vX.Y.Z` |
   | `projections` | `src/Projections` | `projections-vX.Y.Z` |
   | `query` | `src/Query` | `query-vX.Y.Z` |
   | `authorizer` | `src/Authorizer` | `authorizer-vX.Y.Z` |
   | `contracts` | `src/Contracts` | `contracts-vX.Y.Z` |
   | `infra` | `infra` | `infra-vX.Y.Z` |

3. La version de chaque composant vit dans un `version.txt` à la racine de son chemin
   (`release-type: simple`). `Directory.Build.props` lit le `version.txt` le plus proche au-dessus
   du projet ; les projets hors composant (`SharedKernel`, `Tools`, tests) prennent `0.1.0`.
4. **Personne n'édite une version à la main** : `version.txt`, `.release-please-manifest.json` et
   les `CHANGELOG.md` sont modifiés uniquement par les PR de release.
5. Tous les composants démarrent à `0.1.0` ; avant `1.0.0`, un changement incompatible incrémente
   la version mineure (`bump-minor-pre-major`). Le passage en `1.0.0` est décidé en phase 11.
6. Les PR sont fusionnées en **squash** : leur titre, vérifié par le workflow `pr-title`, devient
   le commit analysé par release-please.

## Conséquences

- Une PR `fix(command): …` ne produit qu'une version PATCH de `command`.
- Le préfixe d'URL `/v1` et le `schema_version` des événements restent indépendants de SemVer.
- **Limite connue** : release-please associe un commit à un composant par chemin. Un changement
  dans `src/SharedKernel` ou `src/Tools` ne déclenche aucune release à lui seul. Si un tel
  changement modifie le comportement d'un service, la PR doit aussi toucher ce service (ou le
  rattachement des outils à un composant sera revu par un nouvel ADR, voir le Journal du plan).
- Les tests ne sont pas versionnés.

## Options écartées

- **Une version unique pour tout le dépôt** : chaque correctif réversionne tous les services et
  brouille la traçabilité des déploiements.
- **Un dépôt par service** : les changements de contrat deviennent non atomiques et la
  coordination des versions est manuelle.
- **Versions calculées par MinVer / GitVersion** : bien adaptés à un seul produit par dépôt, moins
  naturels pour des tags par composant avec changelog généré.
