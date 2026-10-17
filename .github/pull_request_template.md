<!--
Titre de la PR : Conventional Commits, portée = composant. Exemples :
  feat(command): ajoute la contre-passation
  fix(projections)!: ...   (changement incompatible)
Le titre devient le message du commit fusionné (squash) et alimente release-please.
-->

## Résumé

<!-- Quoi et pourquoi, en quelques lignes. -->

## Phase du plan

<!-- Ex. Phase 0 — Fondations du dépôt (docs/plan/PLAN.md) -->

## Composants touchés

- [ ] `command`
- [ ] `projections`
- [ ] `query`
- [ ] `authorizer`
- [ ] `contracts`
- [ ] `infra`
- [ ] Autre (SharedKernel, Tools, CI, documentation)

## Définition de « terminé » (CLAUDE.md)

- [ ] Le code compile sans avertissement (`dotnet build Ledger.slnx -warnaserror`).
- [ ] Les tests, y compris d'architecture, passent localement.
- [ ] Les critères d'acceptation de la phase dans `docs/plan/PLAN.md` sont vérifiés.
- [ ] Les cases de la phase sont cochées dans `PLAN.md` et le Journal est mis à jour.
- [ ] Toute décision structurante est consignée dans un ADR (`docs/adr/NNNN-titre.md`).

## Points d'attention pour la revue

<!-- Changement incompatible, migration, impact sur un contrat (OpenAPI, événements), sécurité. -->
