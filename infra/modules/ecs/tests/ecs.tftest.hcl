# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {}

variables {
  prefixe         = "ledger-dev"
  cle_kms_ecs_arn = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-6666-4a5b-9c3d-0123456789ab"
}

run "stockage_ephemere_chiffre_par_cmk" {
  command = apply

  assert {
    condition     = aws_ecs_cluster.grappe.configuration[0].managed_storage_configuration[0].fargate_ephemeral_storage_kms_key_id == var.cle_kms_ecs_arn
    error_message = "Le stockage éphémère Fargate doit être chiffré par la CMK « ecs »."
  }

  assert {
    condition     = one([for s in aws_ecs_cluster.grappe.setting : s.value if s.name == "containerInsights"]) == "enabled"
    error_message = "Container Insights doit être actif."
  }

  assert {
    condition     = aws_ecs_cluster_capacity_providers.grappe.capacity_providers == toset(["FARGATE"])
    error_message = "Phase 7 : Fargate seulement."
  }
}

run "refuse_une_cle_geree_par_aws" {
  command = plan

  variables {
    cle_kms_ecs_arn = "arn:aws:kms:ca-central-1:111122223333:alias/aws/ecs"
  }

  expect_failures = [var.cle_kms_ecs_arn]
}
