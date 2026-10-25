# Tâche ECS ponctuelle des migrations (Ledger.Migrations, DbUp), lancée par le pipeline avant
# chaque déploiement (phase 10 : run-task, puis attente du code de sortie). Connexion TLS
# VerifyFull avec le rôle maître, dont le secret est géré et renouvelé par RDS ; aucun secret en
# variable d'environnement en clair. Aucun rôle de tâche : le programme n'appelle aucun service AWS.

data "aws_ec2_managed_prefix_list" "s3" {
  name = "com.amazonaws.${var.region}.s3"
}

locals {
  nom = "${var.prefixe}-migrations"
}

resource "aws_cloudwatch_log_group" "migrations" {
  name              = "/${var.prefixe}/migrations"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

# --- Rôle d'exécution (agent ECS : tirage de l'image, secrets, journaux) ---------------------

resource "aws_iam_role" "execution" {
  name                 = "${local.nom}-execution"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn

  assume_role_policy = jsonencode({
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

resource "aws_iam_role_policy" "execution" {
  name = "execution-migrations"
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
        Resource = "${aws_cloudwatch_log_group.migrations.arn}:*"
      },
      {
        Sid      = "SecretMaitre"
        Effect   = "Allow"
        Action   = "secretsmanager:GetSecretValue"
        Resource = var.secret_maitre_arn
      },
      {
        Sid       = "DechiffrementSecret"
        Effect    = "Allow"
        Action    = "kms:Decrypt"
        Resource  = var.cle_kms_secrets_arn
        Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.${var.region}.amazonaws.com" } }
      },
    ]
  })
}

# --- Réseau : sortie vers Aurora et les points de terminaison seulement ---------------------

resource "aws_security_group" "migrations" {
  #checkov:skip=CKV2_AWS_5:Attaché à chaque lancement de la tâche (run-task, configuration réseau en sortie), pas à une ressource Terraform.
  name        = local.nom
  description = "Tache de migrations : sortie vers Aurora et les points de terminaison VPC"
  vpc_id      = var.vpc_id

  tags = {
    Name = local.nom
  }
}

resource "aws_vpc_security_group_egress_rule" "vers_aurora" {
  security_group_id            = aws_security_group.migrations.id
  description                  = "PostgreSQL (TLS) vers Aurora"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = var.groupe_securite_aurora
}

resource "aws_vpc_security_group_egress_rule" "vers_points_de_terminaison" {
  security_group_id            = aws_security_group.migrations.id
  description                  = "HTTPS vers les points de terminaison VPC (ECR, Secrets Manager, Logs)"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = var.groupe_securite_points_de_terminaison
}

resource "aws_vpc_security_group_egress_rule" "vers_s3" {
  security_group_id = aws_security_group.migrations.id
  description       = "HTTPS vers S3 (couches des images ECR, passerelle S3)"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  prefix_list_id    = data.aws_ec2_managed_prefix_list.s3.id
}

# Règle du client posée sur le groupe d'Aurora (le module aurora ne connaît pas ses clients).
resource "aws_vpc_security_group_ingress_rule" "aurora_depuis_migrations" {
  security_group_id            = var.groupe_securite_aurora
  description                  = "PostgreSQL depuis la tache de migrations"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = aws_security_group.migrations.id
}

# --- Définition de tâche ------------------------------------------------------------------

resource "aws_ecs_task_definition" "migrations" {
  family                   = local.nom
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.execution.arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  container_definitions = jsonencode([{
    name                   = "migrations"
    image                  = "${var.depot_ecr_url}:${var.version_image}"
    essential              = true
    readonlyRootFilesystem = true
    user                   = "1654"
    environment = [
      { name = "LEDGER_MIGRATIONS_HOTE", value = var.hote_base },
      { name = "LEDGER_MIGRATIONS_BASE", value = var.nom_base },
    ]
    secrets = [
      { name = "LEDGER_MIGRATIONS_UTILISATEUR", valueFrom = "${var.secret_maitre_arn}:username::" },
      { name = "LEDGER_MIGRATIONS_MOT_DE_PASSE", valueFrom = "${var.secret_maitre_arn}:password::" },
    ]
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        "awslogs-group"         = aws_cloudwatch_log_group.migrations.name
        "awslogs-region"        = var.region
        "awslogs-stream-prefix" = "migrations"
      }
    }
  }])
}
