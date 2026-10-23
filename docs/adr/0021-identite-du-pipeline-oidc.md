# 0021 — Identité du pipeline : OIDC GitHub, rôles de plan et d'apply, frontière de permissions

- **Statut** : proposé
- **Date** : 2026-10-21
- **Phase** : 6

## Contexte

Le pipeline GitHub Actions est le seul à appliquer l'infrastructure (CLAUDE.md). Il ne doit
détenir aucun identifiant AWS durable, la production doit exiger une approbation, et un compte
compromis du pipeline ne doit pas pouvoir s'octroyer davantage que ce qu'il gère, ni détruire
l'état Terraform. Un rôle doté d'`AdministratorAccess` ne satisfait pas ces exigences.

## Décision

1. **Fédération OIDC** (`token.actions.githubusercontent.com`, audience `sts.amazonaws.com`) :
   jetons de courte durée (1 h au plus), aucune clé d'accès. GitHub ne stocke que des numéros
   de compte et la région, en **variables** de dépôt.
2. **`bootstrap/gha-plan`** : assumable uniquement avec `sub = repo:<org>/<dépôt>:pull_request`.
   Politique gérée `ReadOnlyAccess`, plus l'écriture des seuls fichiers de verrou de l'état et
   l'usage de la clé de l'état. Le job de plan **ne déclare aucun environnement GitHub** : sinon
   son jeton porterait `environment:<env>`.
3. **`bootstrap/gha-apply`** : assumable uniquement avec
   `sub = repo:<org>/<dépôt>:environment:<env>` (`dev` dans le compte dev, `prod` dans le
   compte prod). L'approbation de `prod` est portée par les règles de protection de
   l'environnement GitHub. Permissions :
   - `PowerUserAccess` (tous les services sauf IAM, Organizations, Account) ;
   - IAM restreint aux rôles et politiques du chemin **`/ledger/`** ; création et modification
     des politiques d'un rôle **conditionnées à `iam:PermissionsBoundary`** =
     `policy/bootstrap/ledger-frontiere-charges` ; retrait de frontière refusé ;
   - refus explicites sur le bucket et la clé de l'état (suppression, politique, versionnage,
     chiffrement, désactivation ou suppression de clé, alias).
4. **Frontière de permissions** `ledger-frontiere-charges` : plafond de tout rôle créé par le
   pipeline (tâches ECS, brokers Kafka et Debezium, Lambda, journaux de flux). Elle énumère les actions de
   données utiles aux charges et **refuse toute action IAM, Organizations et Account**. Elle
   n'accorde rien seule : la permission effective est l'intersection avec la politique du rôle.
5. Les ressources de l'amorçage vivent sous `/bootstrap/`, hors du chemin géré par le
   pipeline ; elles ne changent que par un apply manuel d'un administrateur.

## Conséquences

- Pas d'escalade de privilèges par IAM : un rôle créé par le pipeline ne peut jamais dépasser
  la frontière, ni la retirer, ni modifier les rôles du pipeline.
- Chaque nouveau besoin d'une charge hors de la frontière (une phase ultérieure) impose une PR
  sur `infra/bootstrap` et un apply par un administrateur : c'est voulu.
- Tout module qui crée un rôle doit recevoir l'ARN de la frontière et utiliser le chemin
  `/ledger/` (le module `reseau` le fait pour les journaux de flux).
- `PowerUserAccess` reste large (CloudTrail, Config, etc.) : des SCP d'organisation pourront
  le resserrer (phase 11).
- `ReadOnlyAccess` permet de lire tous les objets S3 du compte : la politique du bucket
  d'archive (phase 7) devra refuser `s3:GetObject` à `gha-plan`.
- Une PR exécute son propre code Terraform avec les droits de lecture de `gha-plan` : seules
  les branches du dépôt (pas les forks) obtiennent un jeton, et l'écriture sur le dépôt est
  réservée aux collaborateurs.
- Le claim `sub` dépend de la configuration OIDC du dépôt. Ce dépôt émet des **sujets
  immuables** (`use_immutable_subject`) : `repo:Xychus@153566096/ComptabiliteApp@1422798619:…`,
  qui portent les identifiants numériques du propriétaire et du dépôt ; un dépôt supprimé puis
  recréé sous le même nom n'hérite donc pas de la confiance. L'amorçage les reçoit par
  `id_organisation_github` / `id_depot_github` (sinon, format par défaut `repo:<org>/<dépôt>`).
  Toute autre personnalisation du sujet casserait les politiques de confiance.

## Options écartées

- **`AdministratorAccess` pour l'apply** : escalade triviale, l'état et ses rôles exposés.
- **Politique d'apply énumérant chaque action de chaque service** : à refaire à chaque phase,
  et fragile ; la frontière ne porte que sur IAM, seul vecteur d'escalade.
- **Clés d'accès dans les secrets GitHub** : identifiants durables, rotation à gérer.
- **Un seul rôle pour plan et apply** : une PR non relue obtiendrait des droits d'écriture.
