# Amorçage d'un compte AWS

Appliqué **une fois par compte** (dev, puis prod), **à la main, par un administrateur**. Le
pipeline ne l'applique jamais et ne peut pas le modifier (ADR 0021).

Crée dans le compte :

| Ressource | Nom (fixe, reconstruit par le pipeline) |
|---|---|
| Bucket de l'état Terraform (versionné, SSE-KMS, accès public bloqué, TLS obligatoire) | `ledger-tfstate-<compte>-<région>` |
| CMK de l'état | `alias/ledger-tfstate` |
| Fournisseur OIDC GitHub | `token.actions.githubusercontent.com` |
| Rôle de plan (lecture seule, pull requests) | `role/bootstrap/gha-plan` |
| Rôle d'apply (environnement GitHub protégé) | `role/bootstrap/gha-apply` |
| Frontière de permissions des rôles créés par le pipeline | `policy/bootstrap/ledger-frontiere-charges` |

## Procédure

Prérequis : Terraform (version de `infra/.terraform-version`), session administrateur dans le
compte cible (ex. `aws sso login`), et le rôle de cette session dans `administrateurs`.

Commandes PowerShell. Une ligne terminée par un accent grave (`` ` ``) continue sur la
suivante (rien après, pas même une espace) ; la ligne `'@` qui ferme un bloc de texte doit
commencer en colonne 0.

```powershell
Set-Location infra\bootstrap
Copy-Item exemple.tfvars dev.tfvars     # renseigner compte, environnement, administrateurs

# 1. Premier apply avec un état local (le bucket n'existe pas encore).
Set-Content -Path backend_override.tf -Encoding utf8NoBOM -Value @'
terraform {
  backend "local" {}
}
'@
terraform init
terraform apply -var-file=dev.tfvars

# 2. Migration de l'état dans le bucket qui vient d'être créé.
Remove-Item backend_override.tf
$COMPTE = "111122223333"
$REGION = "ca-central-1"
terraform init -migrate-state `
  "-backend-config=bucket=ledger-tfstate-$COMPTE-$REGION" `
  "-backend-config=region=$REGION" `
  "-backend-config=kms_key_id=arn:aws:kms:${REGION}:${COMPTE}:alias/ledger-tfstate"
terraform plan -var-file=dev.tfvars    # doit répondre « No changes » avant de supprimer l'état local
Remove-Item terraform.tfstate, terraform.tfstate.backup -ErrorAction SilentlyContinue
```

`${REGION}:${COMPTE}` garde ses accolades : sans elles, PowerShell lirait `$REGION:` comme une
variable à portée (à la manière de `$env:`).

Si l'init répond `The backend configuration argument "kms_key_id" … is not expected for the
selected backend type`, c'est que `backend_override.tf` est toujours présent (backend `local`) :
le supprimer et relancer l'init. L'état local n'est pas perdu tant qu'on ne l'a pas supprimé.

Si le compte possède déjà un fournisseur OIDC GitHub, l'importer avant l'apply :
`terraform import -var-file=dev.tfvars aws_iam_openid_connect_provider.github arn:aws:iam::<compte>:oidc-provider/token.actions.githubusercontent.com`.

Recommencer pour le compte de production avec `environnement = "prod"`.

## Côté GitHub (une fois)

1. **Variables du dépôt** (*Settings → Secrets and variables → Actions → Variables*) — des
   numéros de compte, aucun identifiant ni secret :
   `AWS_REGION` (`ca-central-1`), `AWS_ACCOUNT_ID_DEV`, `AWS_ACCOUNT_ID_PROD`.
   Tant qu'une variable de compte est absente, le plan de cet environnement est ignoré.
2. **Environnements** `dev` et `prod` (*Settings → Environments*) : branches de déploiement
   limitées à `main` ; pour `prod`, relecteurs obligatoires. Seuls les jobs de ces
   environnements peuvent assumer `gha-apply` (`infra-apply.yml` : dev en phase 7, prod en phase 10).
3. Le claim `sub` des jetons OIDC doit correspondre à l'amorçage. Le lire avec
   `gh api repos/<org>/<dépôt>/actions/oidc/customization/sub` : si `use_immutable_subject` vaut
   `true`, renseigner `id_organisation_github` et `id_depot_github` (les nombres après `@` dans
   `sub_claim_prefix`) ; sinon les laisser absents. Un écart donne
   `Not authorized to perform sts:AssumeRoleWithWebIdentity` dans le job de plan.

## Modifier l'amorçage

Toute modification (par exemple élargir la frontière de permissions pour une phase
ultérieure) passe par une PR, puis un apply manuel par un administrateur :

```powershell
terraform init "-backend-config=..."   # mêmes paramètres que ci-dessus
terraform plan -var-file=dev.tfvars
terraform apply -var-file=dev.tfvars
```
