# Infrastructure (Terraform)

Composant release-please `infra` (version dans `version.txt`, gérée par release-please).

Aucun `terraform apply` hors du pipeline GitHub Actions (voir `CLAUDE.md`), sauf l'amorçage,
appliqué à la main par un administrateur.

| Répertoire | Rôle |
|---|---|
| `bootstrap/` | Amorçage d'un compte : état Terraform, OIDC GitHub, rôles `gha-plan` / `gha-apply`, frontière de permissions ([README](bootstrap/README.md), ADR 0011, 0021) |
| `modules/kms` | Une CMK par domaine de données, politique au moindre privilège (ADR 0022) |
| `modules/reseau` | VPC privé sur 3 zones, points de terminaison VPC, journaux de flux (ADR 0023) |
| `modules/ecr` | Un dépôt par service, étiquettes immuables, CMK, analyse à la poussée |
| `envs/{dev,prod}` | Composition des modules ; un compte AWS par environnement |
| `checkov/verifications` | Vérifications Checkov maison : CMK explicite, jamais de clé `aws/*` |

Les modules n'acceptent qu'un ARN de clé KMS (`key/…`) : un alias, a fortiori `alias/aws/*`,
est refusé par la validation des variables.

## Vérifications locales

```bash
terraform fmt -check -recursive infra
terraform -chdir=infra/envs/dev init -backend=false && terraform -chdir=infra/envs/dev validate
terraform -chdir=infra/modules/kms init -backend=false && terraform -chdir=infra/modules/kms test   # idem reseau, ecr, bootstrap
tflint --init --config "$PWD/infra/.tflint.hcl"
tflint --recursive --chdir infra --config "$PWD/infra/.tflint.hcl"
checkov -d infra                     # depuis la racine du dépôt (.checkov.yaml)
```

Les tests `terraform test` utilisent un fournisseur simulé (`mock_provider`) : aucun appel AWS.
Le workflow `infra-ci.yml` exécute tout cela, puis publie le `terraform plan` de chaque
environnement en commentaire de la PR (rôle `gha-plan`, OIDC).
