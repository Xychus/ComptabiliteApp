# Purge planifiée de l'outbox (ADR 0020) : chaque jour, EventBridge lance une tâche Fargate de
# l'image des migrations avec la commande « purger-outbox ». La tâche se connecte avec le rôle
# ledger_maintenance par authentification IAM (ADR 0029, rds-db:connect) en TLS VerifyFull :
# aucun mot de passe. La fonction refuse de purger si le slot du connecteur est absent.

data "aws_ec2_managed_prefix_list" "s3" {
  name = "com.amazonaws.${var.region}.s3"
}

locals {
  nom   = "${var.prefixe}-purge-outbox"
  actif = var.version_image != null

  confiance_taches = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "ecs-tasks.amazonaws.com" }
      Action    = "sts:AssumeRole"
      Condition = {
        StringEquals = { "aws:SourceAccount" = var.compte_aws }
        ArnLike      = { "aws:SourceArn" = "arn:aws:ecs:${var.region}:${var.compte_aws}:*" }
      }
    }]
  })
}

resource "aws_cloudwatch_log_group" "purge" {
  name              = "/${var.prefixe}/purge-outbox"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

resource "aws_security_group" "purge" {
  name        = local.nom
  description = "Purge de l outbox : sortie vers Aurora et les points de terminaison"
  vpc_id      = var.vpc_id

  tags = {
    Name = local.nom
  }
}

resource "aws_vpc_security_group_egress_rule" "vers_aurora" {
  security_group_id            = aws_security_group.purge.id
  description                  = "PostgreSQL (TLS) vers Aurora"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = var.groupe_securite_aurora
}

resource "aws_vpc_security_group_egress_rule" "vers_points_de_terminaison" {
  security_group_id            = aws_security_group.purge.id
  description                  = "HTTPS vers les points de terminaison VPC"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = var.groupe_securite_points_de_terminaison
}

resource "aws_vpc_security_group_egress_rule" "vers_s3" {
  security_group_id = aws_security_group.purge.id
  description       = "HTTPS vers S3 (couches des images ECR)"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  prefix_list_id    = data.aws_ec2_managed_prefix_list.s3.id
}

resource "aws_vpc_security_group_ingress_rule" "aurora_depuis_purge" {
  security_group_id            = var.groupe_securite_aurora
  description                  = "PostgreSQL depuis la purge de l outbox"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = aws_security_group.purge.id
}

# --- Rôles ---------------------------------------------------------------------------------

resource "aws_iam_role" "execution" {
  name                 = "${local.nom}-execution"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn
  assume_role_policy   = local.confiance_taches
}

resource "aws_iam_role_policy" "execution" {
  name = "execution-purge"
  role = aws_iam_role.execution.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "JetonEcr"
        Effect   = "Allow"
        Action   = "ecr:GetAuthorizationToken"
        Resource = "*"
      },
      {
        Sid      = "TirageImage"
        Effect   = "Allow"
        Action   = ["ecr:BatchCheckLayerAvailability", "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer"]
        Resource = var.depot_ecr_arn
      },
      {
        Sid      = "Journaux"
        Effect   = "Allow"
        Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
        Resource = "${aws_cloudwatch_log_group.purge.arn}:*"
      },
    ]
  })
}

resource "aws_iam_role" "tache" {
  name                 = "${local.nom}-tache"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn
  assume_role_policy   = local.confiance_taches
}

resource "aws_iam_role_policy" "tache" {
  name = "connexion-iam"
  role = aws_iam_role.tache.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid      = "ConnexionLedgerMaintenance"
      Effect   = "Allow"
      Action   = "rds-db:connect"
      Resource = "arn:aws:rds-db:${var.region}:${var.compte_aws}:dbuser:${var.identifiant_ressource_aurora}/ledger_maintenance"
    }]
  })
}

# EventBridge lance la tâche : RunTask sur cette famille, passage de ses deux rôles à ECS.
resource "aws_iam_role" "planificateur" {
  name                 = "${local.nom}-planificateur"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "events.amazonaws.com" }
      Action    = "sts:AssumeRole"
      Condition = { StringEquals = { "aws:SourceAccount" = var.compte_aws } }
    }]
  })
}

resource "aws_iam_role_policy" "planificateur" {
  name = "lancement-purge"
  role = aws_iam_role.planificateur.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "LancementTache"
        Effect    = "Allow"
        Action    = "ecs:RunTask"
        Resource  = "arn:aws:ecs:${var.region}:${var.compte_aws}:task-definition/${local.nom}:*"
        Condition = { ArnEquals = { "ecs:cluster" = var.arn_grappe } }
      },
      {
        Sid       = "PassageRoles"
        Effect    = "Allow"
        Action    = "iam:PassRole"
        Resource  = [aws_iam_role.execution.arn, aws_iam_role.tache.arn]
        Condition = { StringEquals = { "iam:PassedToService" = "ecs-tasks.amazonaws.com" } }
      },
    ]
  })
}

# --- Tâche et planification ----------------------------------------------------------------

resource "aws_ecs_task_definition" "purge" {
  count = local.actif ? 1 : 0

  family                   = local.nom
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.tache.arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  container_definitions = jsonencode([{
    name                   = "purge"
    image                  = "${var.depot_ecr_url}:${var.version_image}"
    essential              = true
    readonlyRootFilesystem = true
    user                   = "1654"
    command                = ["purger-outbox"]
    environment = [
      { name = "LEDGER_PURGE_HOTE", value = var.hote_aurora },
      { name = "LEDGER_PURGE_BASE", value = var.nom_base },
      { name = "LEDGER_PURGE_REGION", value = var.region },
      { name = "LEDGER_PURGE_CERTIFICAT_RACINE", value = "/app/rds-ca-global.pem" },
      { name = "LEDGER_PURGE_RETENTION_HEURES", value = tostring(var.retention_heures) },
      { name = "LEDGER_PURGE_SLOT", value = "ledger_outbox" },
    ]
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        "awslogs-group"         = aws_cloudwatch_log_group.purge.name
        "awslogs-region"        = var.region
        "awslogs-stream-prefix" = "purge"
      }
    }
  }])
}

resource "aws_cloudwatch_event_rule" "purge" {
  count = local.actif ? 1 : 0

  name                = local.nom
  description         = "Purge quotidienne de l outbox (ledger.purger_outbox)"
  schedule_expression = var.planification
}

resource "aws_cloudwatch_event_target" "purge" {
  count = local.actif ? 1 : 0

  rule     = aws_cloudwatch_event_rule.purge[0].name
  arn      = var.arn_grappe
  role_arn = aws_iam_role.planificateur.arn

  ecs_target {
    task_definition_arn = aws_ecs_task_definition.purge[0].arn
    launch_type         = "FARGATE"
    task_count          = 1

    network_configuration {
      subnets          = var.sous_reseaux
      security_groups  = [aws_security_group.purge.id]
      assign_public_ip = false
    }
  }
}

# --- Surveillance : une purge réussie au moins tous les deux jours --------------------------

resource "aws_cloudwatch_log_metric_filter" "purge_reussie" {
  name           = "${local.nom}-reussie"
  log_group_name = aws_cloudwatch_log_group.purge.name
  pattern        = "\"ligne(s) purgée(s)\""

  metric_transformation {
    name      = "PurgesReussies"
    namespace = "Ledger/Cdc"
    value     = "1"
  }
}

resource "aws_cloudwatch_metric_alarm" "purge_absente" {
  count = local.actif ? 1 : 0

  alarm_name          = "${local.nom}-absente"
  alarm_description   = "Aucune purge reussie de l outbox depuis deux jours."
  namespace           = "Ledger/Cdc"
  metric_name         = "PurgesReussies"
  statistic           = "Sum"
  period              = 86400
  evaluation_periods  = 2
  comparison_operator = "LessThanThreshold"
  threshold           = 1
  treat_missing_data  = "breaching"
}
