# Environnement « prod » : composition des modules de fondation (phase 6) et de données
# (phase 7). Les rôles et la frontière de permissions viennent de l'amorçage du compte
# (infra/bootstrap), désignés par convention de nom.

locals {
  environnement = "prod"
  prefixe       = "ledger-${local.environnement}"

  arn_role_apply = "arn:aws:iam::${var.compte_aws}:role/bootstrap/gha-apply"
  arn_role_plan  = "arn:aws:iam::${var.compte_aws}:role/bootstrap/gha-plan"
  arn_frontiere  = "arn:aws:iam::${var.compte_aws}:policy/bootstrap/ledger-frontiere-charges"

  # Services du dépôt, puis copies des images officielles tierces (ADR 0028, images.yml).
  depots_ecr = [
    "command-api", "query-api", "projections-worker", "migrations", "pki-lambda", "pki-sentinelle",
    "kafka", "debezium-connect", "aws-otel-collector",
  ]
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

  depots_lambda        = ["pki-lambda"]
  arn_fonctions_lambda = "arn:aws:lambda:${var.region}:${var.compte_aws}:function:${local.prefixe}-*"
}

# --- Données (phase 7) -------------------------------------------------------------------------

module "aurora" {
  source = "../../modules/aurora"

  prefixe              = local.prefixe
  vpc_id               = module.reseau.vpc_id
  sous_reseaux         = module.reseau.sous_reseaux_donnees
  classe_instance      = var.classe_instance_aurora
  cle_kms_aurora_arn   = module.kms.arns["aurora"]
  cle_kms_secrets_arn  = module.kms.arns["secrets"]
  cle_kms_journaux_arn = module.kms.arns["logs"]
}

module "archive" {
  source = "../../modules/archive"

  prefixe       = local.prefixe
  compte_aws    = var.compte_aws
  region        = var.region
  cle_kms_arn   = module.kms.arns["s3-archive"]
  role_plan_arn = local.arn_role_plan
}

module "ecs" {
  source = "../../modules/ecs"

  prefixe         = local.prefixe
  cle_kms_ecs_arn = module.kms.arns["ecs"]
}

module "migrations" {
  source = "../../modules/migrations"

  prefixe                               = local.prefixe
  region                                = var.region
  compte_aws                            = var.compte_aws
  vpc_id                                = module.reseau.vpc_id
  sous_reseaux                          = module.reseau.sous_reseaux_applications
  depot_ecr_url                         = module.ecr.urls["migrations"]
  depot_ecr_arn                         = module.ecr.arns["migrations"]
  version_image                         = var.version_image_migrations
  hote_base                             = module.aurora.point_de_terminaison_redacteur
  nom_base                              = module.aurora.nom_base
  groupe_securite_aurora                = module.aurora.groupe_securite
  groupe_securite_points_de_terminaison = module.reseau.groupe_securite_points_de_terminaison
  secret_maitre_arn                     = module.aurora.secret_maitre_arn
  cle_kms_secrets_arn                   = module.kms.arns["secrets"]
  cle_kms_journaux_arn                  = module.kms.arns["logs"]
  frontiere_permissions_arn             = local.arn_frontiere
}
