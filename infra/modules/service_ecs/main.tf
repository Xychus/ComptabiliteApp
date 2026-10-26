# Service .NET sur ECS Fargate (phase 8) : image par version SemVer exacte, secrets injectés par
# ECS depuis Secrets Manager (jamais en clair), rôle de tâche au moindre privilège, système de
# fichiers racine en lecture seule, utilisateur non root (1654, images chiseled), HTTPS par
# Kestrel avec le certificat de la PKI interne, déploiement avec disjoncteur et retour arrière,
# mise à l'échelle sur le processeur. Collecteur ADOT en conteneur compagnon : traces vers X-Ray
# (CMK « xray »), métriques en EMF dans un groupe de journaux chiffré (CMK « logs »).

data "aws_ec2_managed_prefix_list" "s3" {
  name = "com.amazonaws.${var.region}.s3"
}

locals {
  nom_complet = "${var.prefixe}-${var.nom}"
  actif       = var.version_image != null
  image       = "${var.depot_ecr_url}:${coalesce(var.version_image, "0.0.0")}"
  image_adot  = "${var.depot_collecteur_url}:${var.version_collecteur}"
  environnement = merge(var.environnement, {
    DOTNET_EnableDiagnostics    = "0"
    OTEL_SERVICE_NAME           = var.nom
    OTEL_EXPORTER_OTLP_ENDPOINT = "http://127.0.0.1:4317"
    OTEL_EXPORTER_OTLP_PROTOCOL = "grpc"
  })

  # Configuration du collecteur : OTLP local seulement ; aucun exportateur vers un groupe de
  # journaux créé par AWS (il ne serait pas chiffré par une CMK).
  configuration_adot = yamlencode({
    receivers = { otlp = { protocols = { grpc = { endpoint = "127.0.0.1:4317" } } } }
    processors = {
      batch = {}
    }
    exporters = {
      awsxray = { region = var.region }
      awsemf = {
        region                           = var.region
        namespace                        = "Ledger/Api"
        log_group_name                   = var.groupe_journaux_metriques
        log_stream_name                  = "${var.nom}/{TaskId}"
        dimension_rollup_option          = "NoDimensionRollup"
        resource_to_telemetry_conversion = { enabled = true }
        metric_declarations = [{
          dimensions            = [["service.name", "http.response.status_code"], ["service.name"]]
          metric_name_selectors = ["^http\\.server\\.request\\.duration$"]
        }]
      }
    }
    service = {
      pipelines = {
        traces  = { receivers = ["otlp"], processors = ["batch"], exporters = ["awsxray"] }
        metrics = { receivers = ["otlp"], processors = ["batch"], exporters = ["awsemf"] }
      }
    }
  })

  journaux = {
    for c in ["service", "collecteur"] : c => {
      logDriver = "awslogs"
      options = {
        "awslogs-group"         = aws_cloudwatch_log_group.service.name
        "awslogs-region"        = var.region
        "awslogs-stream-prefix" = c
      }
    }
  }
}

resource "aws_cloudwatch_log_group" "service" {
  name              = "/${var.prefixe}/${var.nom}"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

# --- Réseau --------------------------------------------------------------------------------

resource "aws_security_group" "service" {
  name        = local.nom_complet
  description = "Service ${var.nom} : entree HTTPS depuis l equilibreur, sorties declarees"
  vpc_id      = var.vpc_id

  tags = {
    Name = local.nom_complet
  }
}

resource "aws_vpc_security_group_egress_rule" "vers_points_de_terminaison" {
  security_group_id            = aws_security_group.service.id
  description                  = "HTTPS vers les points de terminaison VPC"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = var.groupe_securite_points_de_terminaison
}

resource "aws_vpc_security_group_egress_rule" "vers_s3" {
  security_group_id = aws_security_group.service.id
  description       = "HTTPS vers S3 (couches des images ECR)"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  prefix_list_id    = data.aws_ec2_managed_prefix_list.s3.id
}

resource "aws_vpc_security_group_egress_rule" "sortie" {
  for_each = var.sorties

  security_group_id            = aws_security_group.service.id
  description                  = "Vers ${each.key} (port ${each.value.port})"
  ip_protocol                  = "tcp"
  from_port                    = each.value.port
  to_port                      = each.value.port
  referenced_security_group_id = each.value.groupe_securite
}

# Règle du client posée sur le groupe de la destination (Aurora, Kafka ne connaissent pas leurs clients).
resource "aws_vpc_security_group_ingress_rule" "entree_destination" {
  for_each = var.sorties

  security_group_id            = each.value.groupe_securite
  description                  = "Depuis ${var.nom}"
  ip_protocol                  = "tcp"
  from_port                    = each.value.port
  to_port                      = each.value.port
  referenced_security_group_id = aws_security_group.service.id
}

resource "aws_vpc_security_group_ingress_rule" "depuis_equilibreur" {
  count = var.groupe_securite_equilibreur != null ? 1 : 0

  security_group_id            = aws_security_group.service.id
  description                  = "HTTPS (Kestrel, PKI interne) depuis l equilibreur de charge"
  ip_protocol                  = "tcp"
  from_port                    = var.port_https
  to_port                      = var.port_https
  referenced_security_group_id = var.groupe_securite_equilibreur
}

resource "aws_vpc_security_group_egress_rule" "equilibreur_vers_service" {
  count = var.groupe_securite_equilibreur != null ? 1 : 0

  security_group_id            = var.groupe_securite_equilibreur
  description                  = "HTTPS vers ${var.nom}"
  ip_protocol                  = "tcp"
  from_port                    = var.port_https
  to_port                      = var.port_https
  referenced_security_group_id = aws_security_group.service.id
}

# --- Rôles ---------------------------------------------------------------------------------

locals {
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

resource "aws_iam_role" "execution" {
  name                 = "${local.nom_complet}-execution"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn
  assume_role_policy   = local.confiance_taches
}

resource "aws_iam_role_policy" "execution" {
  name = "execution"
  role = aws_iam_role.execution.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat(
      [
        {
          Sid      = "JetonEcr"
          Effect   = "Allow"
          Action   = "ecr:GetAuthorizationToken"
          Resource = "*"
        },
        {
          Sid      = "TirageImages"
          Effect   = "Allow"
          Action   = ["ecr:BatchCheckLayerAvailability", "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer"]
          Resource = [var.depot_ecr_arn, var.depot_collecteur_arn]
        },
        {
          Sid      = "Journaux"
          Effect   = "Allow"
          Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
          Resource = "${aws_cloudwatch_log_group.service.arn}:*"
        },
      ],
      [for d in [
        {
          Sid      = "Secrets"
          Effect   = "Allow"
          Action   = "secretsmanager:GetSecretValue"
          Resource = var.arn_secrets_lus
        },
        {
          Sid       = "DechiffrementSecrets"
          Effect    = "Allow"
          Action    = "kms:Decrypt"
          Resource  = var.cle_kms_secrets_arn
          Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.${var.region}.amazonaws.com" } }
        },
      ] : d if length(var.arn_secrets_lus) > 0],
    )
  })
}

resource "aws_iam_role" "tache" {
  name                 = "${local.nom_complet}-tache"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn
  assume_role_policy   = local.confiance_taches
}

resource "aws_iam_role_policy" "tache" {
  name = "tache"
  role = aws_iam_role.tache.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat(
      [
        # Collecteur ADOT : traces (X-Ray, CMK « xray ») et métriques EMF (groupe de journaux dédié).
        {
          Sid      = "Traces"
          Effect   = "Allow"
          Action   = ["xray:PutTelemetryRecords", "xray:PutTraceSegments"]
          Resource = "*"
        },
        {
          Sid       = "ChiffrementTraces"
          Effect    = "Allow"
          Action    = ["kms:Decrypt", "kms:GenerateDataKey"]
          Resource  = var.cle_kms_xray_arn
          Condition = { StringEquals = { "kms:ViaService" = "xray.${var.region}.amazonaws.com" } }
        },
        {
          Sid      = "MetriquesEmf"
          Effect   = "Allow"
          Action   = ["logs:CreateLogStream", "logs:DescribeLogStreams", "logs:PutLogEvents"]
          Resource = ["arn:aws:logs:${var.region}:${var.compte_aws}:log-group:${var.groupe_journaux_metriques}", "arn:aws:logs:${var.region}:${var.compte_aws}:log-group:${var.groupe_journaux_metriques}:*"]
        },
      ],
      var.declarations_role_tache,
    )
  })
}

# --- Tâche et service ----------------------------------------------------------------------

resource "aws_ecs_task_definition" "service" {
  count = local.actif ? 1 : 0

  family                   = local.nom_complet
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = var.cpu
  memory                   = var.memoire
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.tache.arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  container_definitions = jsonencode([
    merge(
      {
        name                   = var.nom
        image                  = local.image
        essential              = true
        user                   = "1654"
        readonlyRootFilesystem = true
        environment            = [for nom in sort(keys(local.environnement)) : { name = nom, value = local.environnement[nom] }]
        secrets                = [for nom in sort(keys(var.secrets)) : { name = nom, valueFrom = var.secrets[nom] }]
        portMappings           = var.port_https != null ? [{ containerPort = var.port_https, protocol = "tcp" }] : []
        dependsOn              = [{ containerName = "collecteur", condition = "START" }]
        logConfiguration       = local.journaux["service"]
      },
      var.commande != null ? { command = var.commande } : {},
    ),
    {
      name                   = "collecteur"
      image                  = local.image_adot
      essential              = false
      readonlyRootFilesystem = true
      environment            = [{ name = "AOT_CONFIG_CONTENT", value = local.configuration_adot }]
      logConfiguration       = local.journaux["collecteur"]
    },
  ])
}

resource "aws_ecs_service" "service" {
  count = local.actif ? 1 : 0

  name            = var.nom
  cluster         = var.arn_grappe
  task_definition = aws_ecs_task_definition.service[0].arn
  desired_count   = var.nombre_taches
  launch_type     = "FARGATE"

  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200
  health_check_grace_period_seconds  = var.groupe_cible_arn != null ? 60 : null

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  network_configuration {
    subnets          = var.sous_reseaux
    security_groups  = [aws_security_group.service.id]
    assign_public_ip = false
  }

  dynamic "load_balancer" {
    for_each = var.groupe_cible_arn != null ? [1] : []

    content {
      target_group_arn = var.groupe_cible_arn
      container_name   = var.nom
      container_port   = var.port_https
    }
  }

  # La mise à l'échelle ajuste le nombre de tâches : Terraform ne le ramène pas au minimum.
  lifecycle {
    ignore_changes = [desired_count]
  }
}

resource "aws_appautoscaling_target" "service" {
  count = local.actif && var.mise_a_echelle != null ? 1 : 0

  service_namespace  = "ecs"
  resource_id        = "service/${var.nom_grappe}/${var.nom}"
  scalable_dimension = "ecs:service:DesiredCount"
  min_capacity       = var.nombre_taches
  max_capacity       = var.mise_a_echelle.maximum

  depends_on = [aws_ecs_service.service]
}

resource "aws_appautoscaling_policy" "processeur" {
  count = local.actif && var.mise_a_echelle != null ? 1 : 0

  name               = "${local.nom_complet}-processeur"
  policy_type        = "TargetTrackingScaling"
  service_namespace  = aws_appautoscaling_target.service[0].service_namespace
  resource_id        = aws_appautoscaling_target.service[0].resource_id
  scalable_dimension = aws_appautoscaling_target.service[0].scalable_dimension

  target_tracking_scaling_policy_configuration {
    target_value       = var.mise_a_echelle.cible_cpu
    scale_in_cooldown  = var.mise_a_echelle.temps_repos
    scale_out_cooldown = var.mise_a_echelle.temps_repos

    predefined_metric_specification {
      predefined_metric_type = "ECSServiceAverageCPUUtilization"
    }
  }
}
