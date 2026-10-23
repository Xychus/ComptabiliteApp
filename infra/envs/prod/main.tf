# Environnement « prod » : composition des modules de fondation (phase 6). Les rôles et la
# frontière de permissions viennent de l'amorçage du compte (infra/bootstrap), désignés par
# convention de nom.

locals {
  environnement = "prod"
  prefixe       = "ledger-${local.environnement}"

  arn_role_apply = "arn:aws:iam::${var.compte_aws}:role/bootstrap/gha-apply"
  arn_frontiere  = "arn:aws:iam::${var.compte_aws}:policy/bootstrap/ledger-frontiere-charges"

  depots_ecr = ["command-api", "query-api", "projections-worker", "migrations"]
}

module "kms" {
  source = "../../modules/kms"

  prefixe         = "ledger"
  compte_aws      = var.compte_aws
  region          = var.region
  administrateurs = concat([local.arn_role_apply], var.administrateurs_kms)
}

module "reseau" {
  source = "../../modules/reseau"

  prefixe                   = local.prefixe
  region                    = var.region
  compte_aws                = var.compte_aws
  cidr_vpc                  = var.cidr_vpc
  zones                     = var.zones
  cle_kms_journaux_arn      = module.kms.arns["logs"]
  frontiere_permissions_arn = local.arn_frontiere
}

module "ecr" {
  source = "../../modules/ecr"

  prefixe     = "ledger"
  depots      = local.depots_ecr
  cle_kms_arn = module.kms.arns["ecr"]
}
