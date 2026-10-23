# Amorçage d'un compte AWS (ADR 0011, ADR 0021) : stockage de l'état Terraform et identité du
# pipeline. Appliqué une fois, à la main, par un administrateur ; jamais par le pipeline.
# Les noms sont fixes : le pipeline les reconstruit à partir du seul numéro de compte.

locals {
  racine            = "arn:aws:iam::${var.compte_aws}:root"
  nom_bucket_etat   = "ledger-tfstate-${var.compte_aws}-${var.region}"
  arn_bucket_etat   = "arn:aws:s3:::${local.nom_bucket_etat}"
  arn_role_plan     = "arn:aws:iam::${var.compte_aws}:role/bootstrap/gha-plan"
  arn_role_apply    = "arn:aws:iam::${var.compte_aws}:role/bootstrap/gha-apply"
  administrateurs   = distinct(concat(var.administrateurs, [local.racine]))
  lecteurs_etat     = concat(local.administrateurs, [local.arn_role_plan, local.arn_role_apply])
  depot_github      = "${var.organisation_github}/${var.depot_github}"
  emetteur_github   = "token.actions.githubusercontent.com"
  contexte_s3_etat  = [local.arn_bucket_etat, "${local.arn_bucket_etat}/*"]
  via_s3            = "s3.${var.region}.amazonaws.com"
  arn_frontiere     = "arn:aws:iam::${var.compte_aws}:policy/bootstrap/ledger-frontiere-charges"
  arn_alias_etat    = "arn:aws:kms:${var.region}:${var.compte_aws}:alias/ledger-tfstate"
  chemin_charges    = "/ledger/"
  arn_roles_charges = "arn:aws:iam::${var.compte_aws}:role${local.chemin_charges}*"
}

# --- Clé de l'état ---------------------------------------------------------------------------

resource "aws_kms_key" "etat" {
  description             = "ledger-tfstate : état Terraform"
  enable_key_rotation     = true
  rotation_period_in_days = 365
  deletion_window_in_days = 30

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "Administration"
        Effect    = "Allow"
        Principal = { AWS = local.racine }
        Action = [
          "kms:CancelKeyDeletion", "kms:CreateAlias", "kms:DeleteAlias", "kms:DescribeKey",
          "kms:DisableKey", "kms:DisableKeyRotation", "kms:EnableKey", "kms:EnableKeyRotation",
          "kms:GetKeyPolicy", "kms:GetKeyRotationStatus", "kms:ListGrants", "kms:ListKeyPolicies",
          "kms:ListKeyRotations", "kms:ListResourceTags", "kms:PutKeyPolicy", "kms:RevokeGrant",
          "kms:RotateKeyOnDemand", "kms:ScheduleKeyDeletion", "kms:TagResource",
          "kms:UntagResource", "kms:UpdateAlias", "kms:UpdateKeyDescription",
        ]
        Resource  = "*"
        Condition = { ArnLike = { "aws:PrincipalArn" = local.administrateurs } }
      },
      {
        Sid       = "LectureMetadonnees"
        Effect    = "Allow"
        Principal = { AWS = local.racine }
        Action    = ["kms:DescribeKey", "kms:GetKeyPolicy", "kms:GetKeyRotationStatus", "kms:ListResourceTags"]
        Resource  = "*"
      },
      {
        # Lecture et écriture de l'état et des fichiers de verrou, via S3 et pour ce bucket seulement.
        Sid       = "ChiffrementEtat"
        Effect    = "Allow"
        Principal = { AWS = local.racine }
        Action    = ["kms:Decrypt", "kms:Encrypt", "kms:GenerateDataKey"]
        Resource  = "*"
        Condition = {
          ArnLike      = { "aws:PrincipalArn" = local.lecteurs_etat, "kms:EncryptionContext:aws:s3:arn" = local.contexte_s3_etat }
          StringEquals = { "kms:ViaService" = local.via_s3, "kms:CallerAccount" = var.compte_aws }
        }
      },
    ]
  })
}

resource "aws_kms_alias" "etat" {
  name          = "alias/ledger-tfstate"
  target_key_id = aws_kms_key.etat.key_id
}

# --- Bucket de l'état ------------------------------------------------------------------------

resource "aws_s3_bucket" "etat" {
  #checkov:skip=CKV_AWS_18:Journalisation d'accès S3 incompatible avec SSE-KMS sur le bucket cible ; accès audités par CloudTrail (événements de données, phase 11).
  #checkov:skip=CKV_AWS_144:Réplication interrégionale traitée avec la reprise après sinistre (phase 11) ; l'état est versionné.
  #checkov:skip=CKV2_AWS_62:Aucun consommateur de notifications pour l'état Terraform.
  bucket        = local.nom_bucket_etat
  force_destroy = false
}

resource "aws_s3_bucket_ownership_controls" "etat" {
  bucket = aws_s3_bucket.etat.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_public_access_block" "etat" {
  bucket                  = aws_s3_bucket.etat.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_versioning" "etat" {
  bucket = aws_s3_bucket.etat.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "etat" {
  bucket = aws_s3_bucket.etat.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm     = "aws:kms"
      kms_master_key_id = aws_kms_key.etat.arn
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "etat" {
  bucket = aws_s3_bucket.etat.id

  rule {
    id     = "versions-anterieures"
    status = "Enabled"

    filter {}

    noncurrent_version_expiration {
      noncurrent_days = var.conservation_versions_etat_jours
    }

    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }

  depends_on = [aws_s3_bucket_versioning.etat]
}

resource "aws_s3_bucket_policy" "etat" {
  bucket = aws_s3_bucket.etat.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "TlsObligatoire"
        Effect    = "Deny"
        Principal = "*"
        Action    = "s3:*"
        Resource  = local.contexte_s3_etat
        Condition = { Bool = { "aws:SecureTransport" = "false" } }
      },
      {
        Sid       = "AccesReserve"
        Effect    = "Deny"
        Principal = "*"
        Action    = "s3:*"
        Resource  = local.contexte_s3_etat
        Condition = { ArnNotLike = { "aws:PrincipalArn" = local.lecteurs_etat } }
      },
      {
        # Les versions antérieures de l'état ne sont supprimées que par le cycle de vie.
        Sid       = "VersionsProtegees"
        Effect    = "Deny"
        Principal = "*"
        Action    = "s3:DeleteObjectVersion"
        Resource  = "${local.arn_bucket_etat}/*"
        Condition = { ArnNotLike = { "aws:PrincipalArn" = local.administrateurs } }
      },
    ]
  })

  depends_on = [aws_s3_bucket_public_access_block.etat]
}

# --- Identité du pipeline GitHub Actions (OIDC) ---------------------------------------------

resource "aws_iam_openid_connect_provider" "github" {
  url            = "https://${local.emetteur_github}"
  client_id_list = ["sts.amazonaws.com"]
}

# Plan : jetons des pull requests du dépôt uniquement (aucun environnement GitHub).
resource "aws_iam_role" "plan" {
  name                 = "gha-plan"
  path                 = "/bootstrap/"
  description          = "Pipeline GitHub : terraform plan en lecture seule"
  max_session_duration = 3600

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Federated = aws_iam_openid_connect_provider.github.arn }
      Action    = "sts:AssumeRoleWithWebIdentity"
      Condition = {
        StringEquals = {
          "${local.emetteur_github}:aud" = "sts.amazonaws.com"
          "${local.emetteur_github}:sub" = "repo:${local.depot_github}:pull_request"
        }
      }
    }]
  })
}

resource "aws_iam_role_policy_attachment" "plan_lecture_seule" {
  role       = aws_iam_role.plan.name
  policy_arn = "arn:aws:iam::aws:policy/ReadOnlyAccess"
}

# Seules écritures du rôle de plan : les fichiers de verrou (use_lockfile), chiffrés par la clé de l'état.
resource "aws_iam_role_policy" "plan_etat" {
  name = "etat-terraform"
  role = aws_iam_role.plan.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "Verrous"
        Effect   = "Allow"
        Action   = ["s3:PutObject", "s3:DeleteObject"]
        Resource = "${local.arn_bucket_etat}/*.tflock"
      },
      {
        Sid      = "CleEtat"
        Effect   = "Allow"
        Action   = ["kms:Decrypt", "kms:GenerateDataKey"]
        Resource = aws_kms_key.etat.arn
      },
    ]
  })
}

# Apply : jetons de l'environnement GitHub protégé de ce compte uniquement.
resource "aws_iam_role" "apply" {
  name                 = "gha-apply"
  path                 = "/bootstrap/"
  description          = "Pipeline GitHub : terraform apply depuis l'environnement ${var.environnement}"
  max_session_duration = 3600

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Federated = aws_iam_openid_connect_provider.github.arn }
      Action    = "sts:AssumeRoleWithWebIdentity"
      Condition = {
        StringEquals = {
          "${local.emetteur_github}:aud" = "sts.amazonaws.com"
          "${local.emetteur_github}:sub" = "repo:${local.depot_github}:environment:${var.environnement}"
        }
      }
    }]
  })
}

# Tous les services, sauf IAM, Organizations et Account (politique gérée par AWS).
resource "aws_iam_role_policy_attachment" "apply_services" {
  role       = aws_iam_role.apply.name
  policy_arn = "arn:aws:iam::aws:policy/PowerUserAccess"
}

# IAM restreint : rôles et politiques du chemin /ledger/ seulement, toujours sous la frontière
# de permissions ; l'amorçage (état, clé, rôles /bootstrap/, frontière) reste hors d'atteinte.
resource "aws_iam_role_policy" "apply_iam" {
  #checkov:skip=CKV_AWS_355:iam:Get*/List* ne se restreignent pas par ressource (lecture des politiques gérées par AWS).
  #checkov:skip=CKV_AWS_290:Les seules écritures sans ressource précise sont les refus (Deny) ; les autres sont limitées au chemin /ledger/.
  #checkov:skip=CKV_AWS_289:Gestion IAM limitée au chemin /ledger/ et conditionnée à la frontière de permissions.
  #checkov:skip=CKV_AWS_286:Escalade bloquée : tout rôle créé ou modifié porte la frontière ledger-frontiere-charges, qui exclut IAM.
  name = "iam-charges"
  role = aws_iam_role.apply.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "RolesSousFrontiere"
        Effect = "Allow"
        Action = [
          "iam:AttachRolePolicy", "iam:CreateRole", "iam:DeleteRolePolicy", "iam:DetachRolePolicy",
          "iam:PutRolePermissionsBoundary", "iam:PutRolePolicy",
        ]
        Resource  = local.arn_roles_charges
        Condition = { StringEquals = { "iam:PermissionsBoundary" = local.arn_frontiere } }
      },
      {
        Sid    = "AdministrationRoles"
        Effect = "Allow"
        Action = [
          "iam:DeleteRole", "iam:TagRole", "iam:UntagRole", "iam:UpdateAssumeRolePolicy",
          "iam:UpdateRole", "iam:UpdateRoleDescription", "iam:PassRole",
        ]
        Resource = local.arn_roles_charges
      },
      {
        Sid    = "Politiques"
        Effect = "Allow"
        Action = [
          "iam:CreatePolicy", "iam:CreatePolicyVersion", "iam:DeletePolicy", "iam:DeletePolicyVersion",
          "iam:SetDefaultPolicyVersion", "iam:TagPolicy", "iam:UntagPolicy",
        ]
        Resource = "arn:aws:iam::${var.compte_aws}:policy${local.chemin_charges}*"
      },
      {
        Sid      = "LectureIam"
        Effect   = "Allow"
        Action   = ["iam:Get*", "iam:List*"]
        Resource = "*"
      },
      {
        Sid      = "FrontiereInamovible"
        Effect   = "Deny"
        Action   = ["iam:DeleteRolePermissionsBoundary"]
        Resource = "*"
      },
      {
        Sid    = "EtatProtege"
        Effect = "Deny"
        Action = [
          "s3:DeleteBucket", "s3:DeleteBucketPolicy", "s3:DeleteObjectVersion", "s3:PutBucketOwnershipControls",
          "s3:PutBucketPolicy", "s3:PutBucketPublicAccessBlock", "s3:PutBucketVersioning",
          "s3:PutEncryptionConfiguration", "s3:PutLifecycleConfiguration",
        ]
        Resource = local.contexte_s3_etat
      },
      {
        Sid    = "CleEtatProtegee"
        Effect = "Deny"
        Action = [
          "kms:CreateGrant", "kms:DeleteAlias", "kms:DisableKey", "kms:DisableKeyRotation",
          "kms:PutKeyPolicy", "kms:ScheduleKeyDeletion", "kms:UpdateAlias",
        ]
        Resource = [aws_kms_key.etat.arn, local.arn_alias_etat]
      },
    ]
  })
}

# --- Frontière de permissions des rôles créés par le pipeline --------------------------------

# Plafond des rôles de charge (tâches ECS, Lambda, MSK Connect, journaux de flux…). Elle
# n'accorde rien par elle-même : la permission effective est l'intersection avec la politique
# du rôle. Toute action IAM, Organizations ou Account en est exclue. À élargir ici (amorçage
# appliqué par un administrateur) si une phase ultérieure en a besoin.
resource "aws_iam_policy" "frontiere" {
  #checkov:skip=CKV_AWS_290:Frontière de permissions : plafond, n'accorde aucune permission par elle-même.
  #checkov:skip=CKV_AWS_355:Frontière de permissions : les ressources sont restreintes par la politique de chaque rôle.
  #checkov:skip=CKV_AWS_289:Frontière de permissions : aucune action de gestion IAM n'est autorisée (refus explicite).
  #checkov:skip=CKV_AWS_287:Frontière de permissions : la lecture des secrets reste soumise à la politique de chaque rôle et à la clé « secrets ».
  #checkov:skip=CKV_AWS_288:Frontière de permissions : la lecture S3 reste soumise à la politique de chaque rôle et aux politiques des buckets.
  name        = "ledger-frontiere-charges"
  path        = "/bootstrap/"
  description = "Frontière de permissions des rôles /ledger/ créés par le pipeline"

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "ServicesDesCharges"
        Effect = "Allow"
        Action = [
          "cloudwatch:PutMetricData",
          "ec2:AssignPrivateIpAddresses", "ec2:CreateNetworkInterface", "ec2:CreateNetworkInterfacePermission",
          "ec2:DeleteNetworkInterface", "ec2:Describe*", "ec2:UnassignPrivateIpAddresses",
          "ecr:BatchCheckLayerAvailability", "ecr:BatchGetImage", "ecr:GetAuthorizationToken", "ecr:GetDownloadUrlForLayer",
          "kafka:Describe*", "kafka:GetBootstrapBrokers", "kafka-cluster:*",
          "kms:Decrypt", "kms:DescribeKey", "kms:Encrypt", "kms:GenerateDataKey*", "kms:ReEncrypt*",
          "logs:CreateLogGroup", "logs:CreateLogStream", "logs:DescribeLogGroups", "logs:DescribeLogStreams", "logs:PutLogEvents",
          "rds-db:connect",
          "s3:AbortMultipartUpload", "s3:GetBucketLocation", "s3:GetObject", "s3:GetObjectVersion", "s3:ListBucket",
          "s3:PutObject", "s3:PutObjectRetention",
          "secretsmanager:DescribeSecret", "secretsmanager:GetRandomPassword", "secretsmanager:GetSecretValue",
          "secretsmanager:PutSecretValue", "secretsmanager:UpdateSecretVersionStage",
          "ssm:GetParameter", "ssm:GetParameters", "ssm:GetParametersByPath",
          "sts:GetCallerIdentity",
          "xray:PutTelemetryRecords", "xray:PutTraceSegments",
        ]
        Resource = "*"
      },
      {
        Sid      = "AucuneGestionDeCompte"
        Effect   = "Deny"
        Action   = ["account:*", "iam:*", "organizations:*"]
        Resource = "*"
      },
    ]
  })
}
