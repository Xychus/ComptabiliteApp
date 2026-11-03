# Paquets de déploiement des Lambda à runtime géré (ADR 0032) : l'autorizer de l'API privée est un
# zip, pas une image. images.yml y dépose « <composant>/<version>.zip » (écriture conditionnelle :
# une version publiée n'est jamais remplacée) ; Terraform désigne la version exacte. Chiffrement
# SSE-KMS par la CMK « lambda » uniquement.

locals {
  nom_bucket = "${var.prefixe}-artefacts-${var.compte_aws}-${var.region}"
  arn_bucket = "arn:aws:s3:::${local.nom_bucket}"
  ressources = [local.arn_bucket, "${local.arn_bucket}/*"]
}

resource "aws_s3_bucket" "artefacts" {
  #checkov:skip=CKV_AWS_18:Journalisation d'accès S3 incompatible avec SSE-KMS sur le bucket cible ; accès audités par CloudTrail (événements de données, phase 11).
  #checkov:skip=CKV_AWS_144:Paquets reconstruits depuis Git par images.yml : aucune réplication interrégionale.
  #checkov:skip=CKV2_AWS_62:Aucun consommateur de notifications : Terraform désigne la version à déployer.
  bucket        = local.nom_bucket
  force_destroy = var.suppression_forcee
}

resource "aws_s3_bucket_ownership_controls" "artefacts" {
  bucket = aws_s3_bucket.artefacts.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_public_access_block" "artefacts" {
  bucket                  = aws_s3_bucket.artefacts.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_versioning" "artefacts" {
  bucket = aws_s3_bucket.artefacts.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "artefacts" {
  bucket = aws_s3_bucket.artefacts.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm     = "aws:kms"
      kms_master_key_id = var.cle_kms_arn
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "artefacts" {
  bucket = aws_s3_bucket.artefacts.id

  rule {
    id     = "televersements-incomplets"
    status = "Enabled"

    filter {}

    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }

  depends_on = [aws_s3_bucket_versioning.artefacts]
}

resource "aws_s3_bucket_policy" "artefacts" {
  bucket = aws_s3_bucket.artefacts.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "TlsObligatoire"
        Effect    = "Deny"
        Principal = "*"
        Action    = "s3:*"
        Resource  = local.ressources
        Condition = { Bool = { "aws:SecureTransport" = "false" } }
      },
      {
        # Sans en-tête, le chiffrement par défaut (la CMK) s'applique ; un en-tête explicite
        # doit désigner SSE-KMS avec cette clé.
        Sid       = "SseKmsSeulement"
        Effect    = "Deny"
        Principal = "*"
        Action    = "s3:PutObject"
        Resource  = "${local.arn_bucket}/*"
        Condition = { StringNotEqualsIfExists = { "s3:x-amz-server-side-encryption" = "aws:kms" } }
      },
      {
        Sid       = "AutreCleRefusee"
        Effect    = "Deny"
        Principal = "*"
        Action    = "s3:PutObject"
        Resource  = "${local.arn_bucket}/*"
        Condition = { StringNotEqualsIfExists = { "s3:x-amz-server-side-encryption-aws-kms-key-id" = var.cle_kms_arn } }
      },
      {
        # Une version publiée ne change jamais : toute écriture exige « If-None-Match: * ».
        Sid       = "EcritureConditionnelleSeulement"
        Effect    = "Deny"
        Principal = "*"
        Action    = "s3:PutObject"
        Resource  = "${local.arn_bucket}/*"
        Condition = { Null = { "s3:if-none-match" = "true" } }
      },
    ]
  })

  depends_on = [aws_s3_bucket_public_access_block.artefacts]
}
