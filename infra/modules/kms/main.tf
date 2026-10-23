# Une CMK par domaine de données (CLAUDE.md, « Sécurité »). Politique de clé au moindre
# privilège (ADR 0022) :
#   - administration réservée aux principaux nommés (condition aws:PrincipalArn), sans aucune
#     action cryptographique ni création d'autorisation (grant) ;
#   - lecture des métadonnées ouverte au compte (filtrée ensuite par les politiques IAM) ;
#   - usage cryptographique uniquement à travers le service AWS du domaine (kms:ViaService), ou,
#     pour les services qui agissent en leur nom propre (CloudWatch Logs, Fargate), par leur
#     principal de service avec un contexte de chiffrement propre au compte.

locals {
  racine = "arn:aws:iam::${var.compte_aws}:root"

  # « services » : préfixes des points de terminaison autorisés par kms:ViaService.
  domaines = {
    aurora       = { description = "Aurora PostgreSQL : journal comptable et projections", services = ["rds"] }
    msk          = { description = "Amazon MSK : transport des événements", services = ["kafka"] }
    "s3-archive" = { description = "S3 : archive d'intégrité (conservation de 6 ans)", services = ["s3"] }
    secrets      = { description = "Secrets Manager : mots de passe et clés applicatives", services = ["secretsmanager"] }
    logs         = { description = "CloudWatch Logs : journaux applicatifs et journaux de flux VPC", services = [] }
    ecr          = { description = "ECR : images des services", services = ["ecr"] }
    ecs          = { description = "ECS Fargate : stockage éphémère des tâches", services = [] }
    lambda       = { description = "Lambda : variables d'environnement", services = ["lambda"] }
    ssm          = { description = "SSM Parameter Store : paramètres SecureString", services = ["ssm"] }
  }

  actions_administration = [
    "kms:CancelKeyDeletion",
    "kms:CreateAlias",
    "kms:DeleteAlias",
    "kms:DescribeKey",
    "kms:DisableKey",
    "kms:DisableKeyRotation",
    "kms:EnableKey",
    "kms:EnableKeyRotation",
    "kms:GetKeyPolicy",
    "kms:GetKeyRotationStatus",
    "kms:ListGrants",
    "kms:ListKeyPolicies",
    "kms:ListKeyRotations",
    "kms:ListResourceTags",
    "kms:PutKeyPolicy",
    "kms:RevokeGrant",
    "kms:RotateKeyOnDemand",
    "kms:ScheduleKeyDeletion",
    "kms:TagResource",
    "kms:UntagResource",
    "kms:UpdateAlias",
    "kms:UpdateKeyDescription",
  ]

  actions_metadonnees = [
    "kms:DescribeKey",
    "kms:GetKeyPolicy",
    "kms:GetKeyRotationStatus",
    "kms:ListGrants",
    "kms:ListKeyPolicies",
    "kms:ListKeyRotations",
    "kms:ListResourceTags",
  ]

  actions_chiffrement = [
    "kms:Decrypt",
    "kms:DescribeKey",
    "kms:Encrypt",
    "kms:GenerateDataKey*",
    "kms:ReEncrypt*",
  ]

  declarations_communes = [
    {
      Sid       = "Administration"
      Effect    = "Allow"
      Principal = { AWS = local.racine }
      Action    = local.actions_administration
      Resource  = "*"
      Condition = { ArnLike = { "aws:PrincipalArn" = distinct(concat(var.administrateurs, [local.racine])) } }
    },
    {
      Sid       = "LectureMetadonnees"
      Effect    = "Allow"
      Principal = { AWS = local.racine }
      Action    = local.actions_metadonnees
      Resource  = "*"
    },
  ]

  declarations_via_service = {
    for domaine, d in local.domaines : domaine => [
      for declaration in [
        {
          Sid       = "UsageViaService"
          Effect    = "Allow"
          Principal = { AWS = local.racine }
          Action    = local.actions_chiffrement
          Resource  = "*"
          Condition = {
            StringEquals = {
              "kms:CallerAccount" = var.compte_aws
              "kms:ViaService"    = [for s in d.services : "${s}.${var.region}.amazonaws.com"]
            }
          }
        },
        {
          Sid       = "AutorisationsViaService"
          Effect    = "Allow"
          Principal = { AWS = local.racine }
          Action    = ["kms:CreateGrant", "kms:ListGrants", "kms:RevokeGrant"]
          Resource  = "*"
          Condition = {
            StringEquals = {
              "kms:CallerAccount" = var.compte_aws
              "kms:ViaService"    = [for s in d.services : "${s}.${var.region}.amazonaws.com"]
            }
            Bool = { "kms:GrantIsForAWSResource" = "true" }
          }
        },
      ] : declaration if length(d.services) > 0
    ]
  }

  # CloudWatch Logs chiffre avec son propre principal : contexte limité aux groupes du compte.
  declarations_logs = [
    {
      Sid       = "CloudWatchLogs"
      Effect    = "Allow"
      Principal = { Service = "logs.${var.region}.amazonaws.com" }
      Action    = local.actions_chiffrement
      Resource  = "*"
      Condition = {
        ArnLike = { "kms:EncryptionContext:aws:logs:arn" = "arn:aws:logs:${var.region}:${var.compte_aws}:log-group:*" }
      }
    },
  ]

  # Stockage éphémère Fargate (documentation ECS) : clés de données sans texte clair et
  # autorisation de déchiffrement, pour les seules grappes du compte portant le préfixe.
  contexte_fargate = {
    "kms:EncryptionContext:aws:ecs:clusterAccount" = var.compte_aws
  }
  declarations_ecs = [
    {
      Sid       = "FargateClesDeDonnees"
      Effect    = "Allow"
      Principal = { Service = "fargate.amazonaws.com" }
      Action    = ["kms:GenerateDataKeyWithoutPlaintext"]
      Resource  = "*"
      Condition = {
        StringEquals = local.contexte_fargate
        StringLike   = { "kms:EncryptionContext:aws:ecs:clusterName" = "${var.prefixe}-*" }
      }
    },
    {
      Sid       = "FargateAutorisations"
      Effect    = "Allow"
      Principal = { Service = "fargate.amazonaws.com" }
      Action    = ["kms:CreateGrant"]
      Resource  = "*"
      Condition = {
        StringEquals                = local.contexte_fargate
        StringLike                  = { "kms:EncryptionContext:aws:ecs:clusterName" = "${var.prefixe}-*" }
        "ForAllValues:StringEquals" = { "kms:GrantOperations" = ["Decrypt"] }
      }
    },
  ]

  declarations_propres = {
    logs = local.declarations_logs
    ecs  = local.declarations_ecs
  }

  politiques = {
    for domaine, _ in local.domaines : domaine => jsonencode({
      Version = "2012-10-17"
      Statement = concat(
        local.declarations_communes,
        local.declarations_via_service[domaine],
        try(local.declarations_propres[domaine], []),
      )
    })
  }
}

resource "aws_kms_key" "cle" {
  for_each = local.domaines

  description             = "${var.prefixe}-${each.key} : ${each.value.description}"
  key_usage               = "ENCRYPT_DECRYPT"
  enable_key_rotation     = true
  rotation_period_in_days = var.periode_rotation_jours
  deletion_window_in_days = var.delai_suppression_jours
  is_enabled              = true
  policy                  = local.politiques[each.key]

  tags = {
    domaine = each.key
  }
}

resource "aws_kms_alias" "alias" {
  for_each = local.domaines

  name          = "alias/${var.prefixe}-${each.key}"
  target_key_id = aws_kms_key.cle[each.key].key_id
}
