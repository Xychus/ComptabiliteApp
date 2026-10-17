# Infrastructure (Terraform)

Composant release-please `infra` (version dans `version.txt`, gérée par release-please).

Le code Terraform arrive à partir de la phase 6 (`docs/plan/PLAN.md`) :
`bootstrap/`, `modules/`, `envs/{dev,prod}/`, `keycloak/`.

Aucun `terraform apply` hors du pipeline GitHub Actions (voir `CLAUDE.md`).
