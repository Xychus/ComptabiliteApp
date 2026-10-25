# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {}

variables {
  prefixe       = "ledger-dev"
  compte_aws    = "111122223333"
  region        = "ca-central-1"
  cle_kms_arn   = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-5555-4a5b-9c3d-0123456789ab"
  role_plan_arn = "arn:aws:iam::111122223333:role/bootstrap/gha-plan"
}

run "verrouillage_compliance_six_ans" {
  command = apply

  assert {
    condition     = aws_s3_bucket.archive.object_lock_enabled && aws_s3_bucket_versioning.archive.versioning_configuration[0].status == "Enabled"
    error_message = "Object Lock et le versionnement sont exigés."
  }

  assert {
    condition     = aws_s3_bucket_object_lock_configuration.archive.rule[0].default_retention[0].mode == "COMPLIANCE" && aws_s3_bucket_object_lock_configuration.archive.rule[0].default_retention[0].years == 6
    error_message = "Conservation par défaut en mode COMPLIANCE pendant 6 ans."
  }

  assert {
    condition     = !aws_s3_bucket.archive.force_destroy
    error_message = "Le bucket d'archive ne doit jamais être vidé par Terraform."
  }

  assert {
    condition = (aws_s3_bucket_public_access_block.archive.block_public_acls && aws_s3_bucket_public_access_block.archive.block_public_policy
    && aws_s3_bucket_public_access_block.archive.ignore_public_acls && aws_s3_bucket_public_access_block.archive.restrict_public_buckets)
    error_message = "Tout accès public doit être bloqué."
  }
}

run "chiffrement_par_la_cmk" {
  command = apply

  assert {
    condition = alltrue([for r in aws_s3_bucket_server_side_encryption_configuration.archive.rule :
      r.apply_server_side_encryption_by_default[0].sse_algorithm == "aws:kms"
      && r.apply_server_side_encryption_by_default[0].kms_master_key_id == var.cle_kms_arn
    && r.bucket_key_enabled])
    error_message = "SSE-KMS par la CMK « s3-archive », avec clé de bucket."
  }
}

run "politique_du_bucket" {
  command = apply

  assert {
    condition     = one([for s in jsondecode(aws_s3_bucket_policy.archive.policy).Statement : s.Condition.Bool["aws:SecureTransport"] if s.Sid == "TlsObligatoire"]) == "false"
    error_message = "Tout transport non TLS doit être refusé."
  }

  assert {
    condition     = one([for s in jsondecode(aws_s3_bucket_policy.archive.policy).Statement : s.Condition.StringNotEqualsIfExists["s3:x-amz-server-side-encryption-aws-kms-key-id"] if s.Sid == "AutreCleRefusee"]) == var.cle_kms_arn
    error_message = "Tout objet chiffré par une autre clé que la CMK doit être refusé."
  }

  assert {
    condition     = contains([for s in jsondecode(aws_s3_bucket_policy.archive.policy).Statement : s.Sid], "CleImpliciteRefusee")
    error_message = "« aws:kms » sans identifiant de clé (clé aws/s3) doit être refusé."
  }

  assert {
    condition = anytrue([for s in jsondecode(aws_s3_bucket_policy.archive.policy).Statement :
    s.Effect == "Deny" && contains(s.Action, "s3:GetObject") && s.Condition.ArnLike["aws:PrincipalArn"] == var.role_plan_arn if s.Sid == "PlanSansLecture"])
    error_message = "Le rôle gha-plan ne doit pas lire les objets archivés."
  }
}

run "refuse_une_conservation_trop_courte" {
  command = plan

  variables {
    conservation_annees = 5
  }

  expect_failures = [var.conservation_annees]
}

run "refuse_une_cle_geree_par_aws" {
  command = plan

  variables {
    cle_kms_arn = "arn:aws:kms:ca-central-1:111122223333:alias/aws/s3"
  }

  expect_failures = [var.cle_kms_arn]
}
