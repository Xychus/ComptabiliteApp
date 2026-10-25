# PKI interne (ADR 0010) : racine OpenSSL hors ligne (hors d'AWS), CA intermédiaire dont la clé
# privée est une clé KMS asymétrique non extractible, certificats finaux de 7 jours émis par une
# Lambda de rotation de Secrets Manager. Aucune ressource AWS Private CA.
#
# Démarrage (runbook pki-ceremonie-annuelle) :
#   1. version_image + generations_intermediaire : clé KMS et Lambda en mode cérémonie ;
#   2. un administrateur obtient la CSR de la Lambda, la fait signer hors ligne par la racine ;
#   3. racine, intermédiaire et génération active versionnées dans Git : rotation activée.

data "aws_ec2_managed_prefix_list" "s3" {
  name = "com.amazonaws.${var.region}.s3"
}

locals {
  nom    = "${var.prefixe}-pki"
  racine = "arn:aws:iam::${var.compte_aws}:root"

  lambda_active      = var.version_image != null
  autorite_installee = var.certificat_intermediaire_pem != null
  rotation_active    = local.lambda_active && local.autorite_installee

  etiquettes_identites = {
    for cle, i in var.identites : cle => merge(
      {
        "pki:cn"    = i.nom_commun
        "pki:usage" = i.usage
      },
      length(i.noms_dns) > 0 ? { "pki:san" = join(" ", i.noms_dns) } : {},
      length(i.services) > 0 ? { "pki:grappe" = var.nom_grappe, "pki:services" = join(" ", i.services) } : {},
      i.certificat_acm_arn != null ? { "pki:acm" = i.certificat_acm_arn } : {},
    )
  }

  certificats_acm = compact([for i in values(var.identites) : i.certificat_acm_arn])
  services_ecs = distinct(concat(
    flatten([for i in values(var.identites) : i.services]),
    flatten([for m in values(var.mots_de_passe_postgres) : m.services]),
  ))

  etiquettes_mots_de_passe = {
    for cle, m in var.mots_de_passe_postgres : cle => merge(
      {
        "rotation:type" = "mot-de-passe-postgres"
        "pg:role"       = m.role
      },
      length(m.services) > 0 ? { "pki:grappe" = var.nom_grappe, "pki:services" = join(" ", m.services) } : {},
    )
  }

  rotation_mots_de_passe = local.lambda_active && var.aurora != null
  acces_aurora           = var.aurora != null && length(var.mots_de_passe_postgres) > 0
}

# --- Clés de l'intermédiaire (une par génération) ----------------------------------------------

resource "aws_kms_key" "intermediaire" {
  #checkov:skip=CKV_AWS_7:Clé asymétrique de signature : KMS ne fait pas tourner ces clés ; une nouvelle génération est créée à chaque cérémonie annuelle.
  for_each = var.generations_intermediaire

  description              = "${local.nom} : cle privee de la CA intermediaire ${each.key} (ADR 0010)"
  customer_master_key_spec = "ECC_NIST_P384"
  key_usage                = "SIGN_VERIFY"
  deletion_window_in_days  = var.delai_suppression_cles_jours
  is_enabled               = true

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "Administration"
        Effect    = "Allow"
        Principal = { AWS = local.racine }
        Action = [
          "kms:CancelKeyDeletion", "kms:CreateAlias", "kms:DeleteAlias", "kms:DescribeKey", "kms:DisableKey",
          "kms:EnableKey", "kms:GetKeyPolicy", "kms:GetPublicKey", "kms:ListKeyPolicies", "kms:ListResourceTags",
          "kms:PutKeyPolicy", "kms:ScheduleKeyDeletion", "kms:TagResource", "kms:UntagResource", "kms:UpdateAlias",
          "kms:UpdateKeyDescription",
        ]
        Resource  = "*"
        Condition = { ArnLike = { "aws:PrincipalArn" = distinct(concat(var.administrateurs_kms, [local.racine])) } }
      },
      {
        Sid       = "LectureMetadonnees"
        Effect    = "Allow"
        Principal = { AWS = local.racine }
        Action    = ["kms:DescribeKey", "kms:GetKeyPolicy", "kms:GetPublicKey", "kms:ListResourceTags"]
        Resource  = "*"
      },
      # Seule la Lambda signe, et seulement des condensés SHA-384 (certificats finaux, CSR).
      {
        Sid       = "SignatureParLaLambda"
        Effect    = "Allow"
        Principal = { AWS = aws_iam_role.lambda.arn }
        Action    = ["kms:Sign", "kms:GetPublicKey"]
        Resource  = "*"
        Condition = {
          StringEquals = {
            "kms:SigningAlgorithm" = "ECDSA_SHA_384"
            "kms:MessageType"      = "DIGEST"
          }
        }
      },
    ]
  })

  tags = {
    domaine    = "pki"
    generation = each.key
  }
}

resource "aws_kms_alias" "intermediaire" {
  for_each = var.generations_intermediaire

  name          = "alias/${local.nom}-intermediaire-${each.key}"
  target_key_id = aws_kms_key.intermediaire[each.key].key_id
}

# --- Secrets des certificats finaux --------------------------------------------------------

resource "aws_secretsmanager_secret" "certificat" {
  #checkov:skip=CKV2_AWS_57:Rotation activée par aws_secretsmanager_secret_rotation dès que l'intermédiaire est installée (mode cérémonie avant).
  for_each = var.identites

  name                    = "${var.prefixe}/pki/${each.key}"
  description             = "Certificat final ${each.value.nom_commun} (cle, chaine, racine), renouvele par la Lambda PKI"
  kms_key_id              = var.cle_kms_secrets_arn
  recovery_window_in_days = var.fenetre_recuperation_secrets_jours

  tags = local.etiquettes_identites[each.key]
}

resource "aws_secretsmanager_secret_rotation" "certificat" {
  for_each = local.rotation_active ? var.identites : {}

  secret_id           = aws_secretsmanager_secret.certificat[each.key].id
  rotation_lambda_arn = aws_lambda_function.pki[0].arn
  rotate_immediately  = true

  rotation_rules {
    automatically_after_days = var.jours_entre_rotations
  }

  depends_on = [aws_lambda_permission.secrets_manager]
}

# --- Mots de passe PostgreSQL (rôles sans authentification IAM) ------------------------------

resource "aws_secretsmanager_secret" "mot_de_passe" {
  #checkov:skip=CKV2_AWS_57:Rotation activée par aws_secretsmanager_secret_rotation dès que la Lambda existe.
  for_each = var.mots_de_passe_postgres

  name                    = "${var.prefixe}/postgres/${each.key}"
  description             = "Mot de passe du role PostgreSQL ${each.value.role}, renouvele par la Lambda PKI"
  kms_key_id              = var.cle_kms_secrets_arn
  recovery_window_in_days = var.fenetre_recuperation_secrets_jours

  tags = local.etiquettes_mots_de_passe[each.key]

  lifecycle {
    precondition {
      condition     = var.aurora != null
      error_message = "« aurora » est exigé pour renouveler un mot de passe PostgreSQL."
    }
  }
}

resource "aws_secretsmanager_secret_rotation" "mot_de_passe" {
  for_each = local.rotation_mots_de_passe ? var.mots_de_passe_postgres : {}

  secret_id           = aws_secretsmanager_secret.mot_de_passe[each.key].id
  rotation_lambda_arn = aws_lambda_function.pki[0].arn
  rotate_immediately  = true

  rotation_rules {
    automatically_after_days = var.jours_entre_rotations
  }

  depends_on = [aws_lambda_permission.secrets_manager]
}

# --- Rôle de la Lambda ---------------------------------------------------------------------

resource "aws_iam_role" "lambda" {
  name                 = "${local.nom}-lambda"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
      Condition = { StringEquals = { "aws:SourceAccount" = var.compte_aws } }
    }]
  })
}

resource "aws_iam_role_policy" "lambda" {
  name = "pki-lambda"
  role = aws_iam_role.lambda.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat(
      [
        {
          Sid      = "Journaux"
          Effect   = "Allow"
          Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
          Resource = "${aws_cloudwatch_log_group.lambda.arn}:*"
        },
        # Interfaces réseau de la Lambda dans le VPC : actions sans ressource précise (documentation Lambda).
        {
          Sid    = "InterfacesReseau"
          Effect = "Allow"
          Action = [
            "ec2:AssignPrivateIpAddresses", "ec2:CreateNetworkInterface", "ec2:DeleteNetworkInterface",
            "ec2:DescribeNetworkInterfaces", "ec2:UnassignPrivateIpAddresses",
          ]
          Resource = "*"
        },
        {
          Sid      = "Traces"
          Effect   = "Allow"
          Action   = ["xray:PutTelemetryRecords", "xray:PutTraceSegments"]
          Resource = "*"
        },
        {
          Sid       = "VariablesChiffrees"
          Effect    = "Allow"
          Action    = "kms:Decrypt"
          Resource  = var.cle_kms_lambda_arn
          Condition = { StringEquals = { "kms:ViaService" = "lambda.${var.region}.amazonaws.com" } }
        },
        {
          Sid       = "MesuresEcheances"
          Effect    = "Allow"
          Action    = "cloudwatch:PutMetricData"
          Resource  = "*"
          Condition = { StringEquals = { "cloudwatch:namespace" = "Ledger/Pki" } }
        },
      ],
      [for d in [
        {
          Sid      = "SignatureIntermediaire"
          Effect   = "Allow"
          Action   = ["kms:Sign", "kms:GetPublicKey"]
          Resource = [for k in aws_kms_key.intermediaire : k.arn]
        },
      ] : d if length(var.generations_intermediaire) > 0],
      [for d in [
        {
          Sid      = "RotationCertificats"
          Effect   = "Allow"
          Action   = ["secretsmanager:DescribeSecret", "secretsmanager:GetSecretValue", "secretsmanager:PutSecretValue", "secretsmanager:UpdateSecretVersionStage"]
          Resource = [for s in aws_secretsmanager_secret.certificat : s.arn]
        },
        {
          Sid       = "ChiffrementSecrets"
          Effect    = "Allow"
          Action    = ["kms:Decrypt", "kms:GenerateDataKey"]
          Resource  = var.cle_kms_secrets_arn
          Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.${var.region}.amazonaws.com" } }
        },
        # Inventaire : ListSecrets n'accepte aucune ressource ; la lecture reste limitée ci-dessus.
        {
          Sid      = "InventaireSecrets"
          Effect   = "Allow"
          Action   = "secretsmanager:ListSecrets"
          Resource = "*"
        },
      ] : d if length(var.identites) > 0],
      [for d in [
        {
          Sid      = "RotationMotsDePasse"
          Effect   = "Allow"
          Action   = ["secretsmanager:DescribeSecret", "secretsmanager:GetSecretValue", "secretsmanager:PutSecretValue", "secretsmanager:UpdateSecretVersionStage"]
          Resource = [for s in aws_secretsmanager_secret.mot_de_passe : s.arn]
        },
        # Secret maître géré par RDS (chiffré par la même CMK « secrets ») : administration des rôles.
        {
          Sid      = "SecretMaitreAurora"
          Effect   = "Allow"
          Action   = "secretsmanager:GetSecretValue"
          Resource = try(var.aurora.secret_maitre_arn, "")
        },
        {
          Sid       = "ChiffrementMotsDePasse"
          Effect    = "Allow"
          Action    = ["kms:Decrypt", "kms:GenerateDataKey"]
          Resource  = var.cle_kms_secrets_arn
          Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.${var.region}.amazonaws.com" } }
        },
      ] : d if local.acces_aurora],
      [for d in [
        {
          Sid      = "ReimportAcm"
          Effect   = "Allow"
          Action   = "acm:ImportCertificate"
          Resource = local.certificats_acm
        },
      ] : d if length(local.certificats_acm) > 0],
      [for d in [
        {
          Sid      = "RedeploiementServices"
          Effect   = "Allow"
          Action   = "ecs:UpdateService"
          Resource = [for s in local.services_ecs : "arn:aws:ecs:${var.region}:${var.compte_aws}:service/${var.nom_grappe}/${s}"]
        },
      ] : d if length(local.services_ecs) > 0],
    )
  })
}

# --- Lambda --------------------------------------------------------------------------------

resource "aws_cloudwatch_log_group" "lambda" {
  name              = "/aws/lambda/${local.nom}"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

resource "aws_security_group" "lambda" {
  name        = "${local.nom}-lambda"
  description = "Lambda PKI : sortie HTTPS vers les points de terminaison VPC seulement"
  vpc_id      = var.vpc_id

  tags = {
    Name = "${local.nom}-lambda"
  }
}

resource "aws_vpc_security_group_egress_rule" "lambda_vers_points_de_terminaison" {
  security_group_id            = aws_security_group.lambda.id
  description                  = "HTTPS vers les points de terminaison VPC (KMS, Secrets Manager, ACM, ECS, CloudWatch)"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = var.groupe_securite_points_de_terminaison
}

# Rotation des mots de passe : PostgreSQL (TLS) vers Aurora ; la règle d'entrée est posée sur le
# groupe d'Aurora (le module aurora ne connaît pas ses clients).
resource "aws_vpc_security_group_egress_rule" "lambda_vers_aurora" {
  count = local.acces_aurora ? 1 : 0

  security_group_id            = aws_security_group.lambda.id
  description                  = "PostgreSQL (TLS) vers Aurora : rotation des mots de passe"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = var.aurora.groupe_securite
}

resource "aws_vpc_security_group_ingress_rule" "aurora_depuis_lambda" {
  count = local.acces_aurora ? 1 : 0

  security_group_id            = var.aurora.groupe_securite
  description                  = "PostgreSQL depuis la Lambda PKI (rotation des mots de passe)"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = aws_security_group.lambda.id
}

resource "aws_vpc_security_group_egress_rule" "lambda_vers_s3" {
  security_group_id = aws_security_group.lambda.id
  description       = "HTTPS vers S3 (passerelle)"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  prefix_list_id    = data.aws_ec2_managed_prefix_list.s3.id
}

resource "aws_lambda_function" "pki" {
  #checkov:skip=CKV_AWS_116:Invocations synchrones (Secrets Manager, EventBridge) : l'échec est signalé à l'appelant et alarmé ; une file de lettres mortes n'ajouterait rien.
  #checkov:skip=CKV_AWS_272:Signature du code : les images sont signées par le pipeline en phase 10 ; une image ECR n'accepte pas la configuration de signature Lambda (paquets zip seulement).
  count = local.lambda_active ? 1 : 0

  function_name = local.nom
  description   = "PKI interne : rotation des certificats finaux, inventaire des echeances, CSR de ceremonie"
  role          = aws_iam_role.lambda.arn
  package_type  = "Image"
  image_uri     = "${var.depot_ecr_url}:${var.version_image}"
  architectures = ["x86_64"]
  memory_size   = 512
  timeout       = 60
  kms_key_arn   = var.cle_kms_lambda_arn

  # Deux rotations simultanées au plus (Secrets Manager traite les secrets indépendamment).
  reserved_concurrent_executions = 2

  environment {
    variables = merge(
      { DOTNET_EnableDiagnostics = "0" },
      local.autorite_installee ? {
        Ledger__Pki__CertificatIntermediaire = var.certificat_intermediaire_pem
        Ledger__Pki__CertificatRacine        = var.certificat_racine_pem
        Ledger__Pki__CleIntermediaire        = aws_kms_key.intermediaire[var.generation_active].arn
      } : {},
      local.acces_aurora ? {
        Ledger__Pki__Postgres__Hote         = var.aurora.hote
        Ledger__Pki__Postgres__Base         = var.aurora.base
        Ledger__Pki__Postgres__SecretMaitre = var.aurora.secret_maitre_arn
      } : {},
    )
  }

  vpc_config {
    subnet_ids         = var.sous_reseaux
    security_group_ids = [aws_security_group.lambda.id]
  }

  tracing_config {
    mode = "Active"
  }

  logging_config {
    log_format = "Text"
    log_group  = aws_cloudwatch_log_group.lambda.name
  }

  depends_on = [aws_iam_role_policy.lambda]
}

resource "aws_lambda_permission" "secrets_manager" {
  count = local.lambda_active ? 1 : 0

  statement_id   = "RotationSecretsManager"
  action         = "lambda:InvokeFunction"
  function_name  = aws_lambda_function.pki[0].function_name
  principal      = "secretsmanager.amazonaws.com"
  source_account = var.compte_aws
}

# --- Inventaire quotidien des échéances -----------------------------------------------------

resource "aws_cloudwatch_event_rule" "inventaire" {
  count = local.rotation_active ? 1 : 0

  name                = "${local.nom}-inventaire"
  description         = "Inventaire quotidien des echeances des certificats (Ledger/Pki)"
  schedule_expression = "rate(1 day)"
}

resource "aws_cloudwatch_event_target" "inventaire" {
  count = local.rotation_active ? 1 : 0

  rule  = aws_cloudwatch_event_rule.inventaire[0].name
  arn   = aws_lambda_function.pki[0].arn
  input = jsonencode({ operation = "inventaire" })
}

resource "aws_lambda_permission" "inventaire" {
  count = local.rotation_active ? 1 : 0

  statement_id  = "InventaireEventBridge"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.pki[0].function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.inventaire[0].arn
}

# --- Alarmes (sans action : sujet SNS en phase 11) -----------------------------------------

resource "aws_cloudwatch_metric_alarm" "echeance_certificats" {
  count = local.rotation_active ? 1 : 0

  alarm_name          = "${local.nom}-echeance-certificats"
  alarm_description   = "Un certificat final expire dans moins de 3 jours : une rotation a echoue (ADR 0010)."
  namespace           = "Ledger/Pki"
  metric_name         = "JoursAvantEcheance"
  dimensions          = { Identite = "minimum-certificats-finaux" }
  statistic           = "Minimum"
  period              = 86400
  evaluation_periods  = 1
  comparison_operator = "LessThanThreshold"
  threshold           = 3
  treat_missing_data  = "breaching"
}

resource "aws_cloudwatch_metric_alarm" "echeance_intermediaire" {
  count = local.rotation_active ? 1 : 0

  alarm_name          = "${local.nom}-echeance-intermediaire"
  alarm_description   = "La CA intermediaire expire dans moins de 30 jours : planifier la ceremonie annuelle."
  namespace           = "Ledger/Pki"
  metric_name         = "JoursAvantEcheance"
  dimensions          = { Identite = "ac-intermediaire" }
  statistic           = "Minimum"
  period              = 86400
  evaluation_periods  = 1
  comparison_operator = "LessThanThreshold"
  threshold           = 30
  treat_missing_data  = "breaching"
}

resource "aws_cloudwatch_metric_alarm" "erreurs_lambda" {
  count = local.lambda_active ? 1 : 0

  alarm_name          = "${local.nom}-erreurs"
  alarm_description   = "Echec de la Lambda PKI (rotation, inventaire ou CSR)."
  namespace           = "AWS/Lambda"
  metric_name         = "Errors"
  dimensions          = { FunctionName = aws_lambda_function.pki[0].function_name }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  comparison_operator = "GreaterThanThreshold"
  threshold           = 0
  treat_missing_data  = "notBreaching"
}
