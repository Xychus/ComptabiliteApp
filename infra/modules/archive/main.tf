# Archive d'intégrité (conservation de 6 ans, Revenu Québec) : bucket S3 en Object Lock, mode
# COMPLIANCE. Personne, pas même l'utilisateur racine, ne peut supprimer ni écraser une version
# d'objet avant l'échéance de sa conservation, ni raccourcir celle-ci. Chiffrement SSE-KMS par la
# CMK « s3-archive » uniquement. Les exports (Ledger.Integrity) arrivent en phase 11.

locals {
  nom_bucket = "${var.prefixe}-archive-${var.compte_aws}-${var.region}"
  arn_bucket = "arn:aws:s3:::${local.nom_bucket}"
  ressources = [local.arn_bucket, "${local.arn_bucket}/*"]
}

resource "aws_s3_bucket" "archive" {
  #checkov:skip=CKV_AWS_18:Journalisation d'accès S3 incompatible avec SSE-KMS sur le bucket cible ; accès audités par CloudTrail (événements de données, phase 11).
  #checkov:skip=CKV_AWS_144:Réplication interrégionale traitée avec la reprise après sinistre (phase 11).
  #checkov:skip=CKV2_AWS_62:Aucun consommateur de notifications pour l'archive (vérification d'intégrité planifiée, phase 11).
  bucket              = local.nom_bucket
  object_lock_enabled = true
  force_destroy       = false
}

resource "aws_s3_bucket_ownership_controls" "archive" {
  bucket = aws_s3_bucket.archive.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_public_access_block" "archive" {
  bucket                  = aws_s3_bucket.archive.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_versioning" "archive" {
  bucket = aws_s3_bucket.archive.id

  versioning_configuration {
    status = "Enabled"
  }
}

# Conservation par défaut de chaque nouvelle version. COMPLIANCE : irréversible, y compris pour
# l'utilisateur racine ; une version écrite (même par erreur) reste facturée jusqu'à l'échéance.
resource "aws_s3_bucket_object_lock_configuration" "archive" {
  bucket = aws_s3_bucket.archive.id

  rule {
    default_retention {
      mode  = "COMPLIANCE"
      years = var.conservation_annees
    }
  }

  depends_on = [aws_s3_bucket_versioning.archive]
}

resource "aws_s3_bucket_server_side_encryption_configuration" "archive" {
  bucket = aws_s3_bucket.archive.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm     = "aws:kms"
      kms_master_key_id = var.cle_kms_arn
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "archive" {
  bucket = aws_s3_bucket.archive.id

  rule {
    id     = "televersements-incomplets"
    status = "Enabled"

    filter {}

    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }

  depends_on = [aws_s3_bucket_versioning.archive]
}

resource "aws_s3_bucket_policy" "archive" {
  bucket = aws_s3_bucket.archive.id

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
        # doit désigner SSE-KMS (ni SSE-S3, ni DSSE-KMS).
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
        # « aws:kms » sans identifiant de clé désignerait la clé gérée par AWS (aws/s3).
        Sid       = "CleImpliciteRefusee"
        Effect    = "Deny"
        Principal = "*"
        Action    = "s3:PutObject"
        Resource  = "${local.arn_bucket}/*"
        Condition = {
          StringEquals = { "s3:x-amz-server-side-encryption" = "aws:kms" }
          Null         = { "s3:x-amz-server-side-encryption-aws-kms-key-id" = "true" }
        }
      },
      {
        # ReadOnlyAccess accorde s3:Get* : le rôle de plan ne lit pas le contenu archivé.
        Sid       = "PlanSansLecture"
        Effect    = "Deny"
        Principal = "*"
        Action    = ["s3:GetObject", "s3:GetObjectVersion", "s3:GetObjectAttributes", "s3:GetObjectVersionAttributes"]
        Resource  = "${local.arn_bucket}/*"
        Condition = { ArnLike = { "aws:PrincipalArn" = var.role_plan_arn } }
      },
    ]
  })

  depends_on = [aws_s3_bucket_public_access_block.archive]
}
