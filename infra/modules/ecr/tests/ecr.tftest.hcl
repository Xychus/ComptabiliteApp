# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {}

variables {
  prefixe     = "ledger"
  depots      = ["command-api", "query-api", "projections-worker", "migrations"]
  cle_kms_arn = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-2222-4a5b-9c3d-0123456789ab"
}

run "depots_immuables_chiffres_par_cmk" {
  command = apply

  assert {
    condition     = toset([for d in aws_ecr_repository.depot : d.name]) == toset(["ledger/command-api", "ledger/query-api", "ledger/projections-worker", "ledger/migrations"])
    error_message = "Un dépôt par service est attendu."
  }

  assert {
    condition     = alltrue([for d in aws_ecr_repository.depot : d.image_tag_mutability == "IMMUTABLE"])
    error_message = "Les étiquettes d'images doivent être immuables."
  }

  assert {
    condition     = alltrue([for d in aws_ecr_repository.depot : d.encryption_configuration[0].encryption_type == "KMS" && d.encryption_configuration[0].kms_key == var.cle_kms_arn])
    error_message = "Chaque dépôt doit être chiffré par la CMK « ecr »."
  }

  assert {
    condition     = alltrue([for d in aws_ecr_repository.depot : !d.force_delete])
    error_message = "Par défaut, un dépôt contenant des images n'est jamais supprimé."
  }

  assert {
    condition     = alltrue([for d in aws_ecr_repository.depot : d.image_scanning_configuration[0].scan_on_push])
    error_message = "L'analyse à la poussée doit être activée."
  }

  assert {
    condition     = alltrue([for p in aws_ecr_lifecycle_policy.depot : jsondecode(p.policy).rules[0].selection.tagStatus == "untagged"])
    error_message = "Seules les images sans étiquette peuvent expirer."
  }
}

run "refuse_une_cle_geree_par_aws" {
  command = plan

  variables {
    cle_kms_arn = "arn:aws:kms:ca-central-1:111122223333:alias/aws/ecr"
  }

  expect_failures = [var.cle_kms_arn]
}
