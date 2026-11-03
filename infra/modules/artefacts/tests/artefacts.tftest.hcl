# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {}

variables {
  prefixe     = "ledger-dev"
  compte_aws  = "111122223333"
  region      = "ca-central-1"
  cle_kms_arn = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-7777-4a5b-9c3d-0123456789ab"
}

run "prive_versionne_chiffre_par_la_cmk" {
  command = apply

  assert {
    condition     = aws_s3_bucket.artefacts.bucket == "ledger-dev-artefacts-111122223333-ca-central-1" && !aws_s3_bucket.artefacts.force_destroy
    error_message = "Nom reconstruit depuis le compte (images.yml) ; jamais vidé par défaut."
  }

  assert {
    condition = (aws_s3_bucket_public_access_block.artefacts.block_public_acls && aws_s3_bucket_public_access_block.artefacts.block_public_policy
    && aws_s3_bucket_public_access_block.artefacts.ignore_public_acls && aws_s3_bucket_public_access_block.artefacts.restrict_public_buckets)
    error_message = "Tout accès public doit être bloqué."
  }

  assert {
    condition     = aws_s3_bucket_versioning.artefacts.versioning_configuration[0].status == "Enabled"
    error_message = "Le bucket est versionné."
  }

  assert {
    condition = alltrue([for r in aws_s3_bucket_server_side_encryption_configuration.artefacts.rule : (
      one(r.apply_server_side_encryption_by_default).sse_algorithm == "aws:kms"
      && one(r.apply_server_side_encryption_by_default).kms_master_key_id == var.cle_kms_arn
    )])
    error_message = "Chiffrement SSE-KMS par la CMK « lambda »."
  }
}

run "tls_cle_et_ecriture_conditionnelle_imposes" {
  command = apply

  assert {
    condition     = toset([for s in jsondecode(aws_s3_bucket_policy.artefacts.policy).Statement : s.Sid if s.Effect == "Deny"]) == toset(["TlsObligatoire", "SseKmsSeulement", "AutreCleRefusee", "EcritureConditionnelleSeulement"])
    error_message = "TLS, CMK et écriture conditionnelle (version jamais remplacée) sont imposés."
  }

  assert {
    condition     = one([for s in jsondecode(aws_s3_bucket_policy.artefacts.policy).Statement : s.Condition.Null["s3:if-none-match"] if s.Sid == "EcritureConditionnelleSeulement"]) == "true"
    error_message = "Une écriture sans If-None-Match est refusée."
  }
}

run "cle_aws_refusee" {
  command = plan

  variables {
    cle_kms_arn = "arn:aws:kms:ca-central-1:111122223333:alias/aws/s3"
  }

  expect_failures = [var.cle_kms_arn]
}
