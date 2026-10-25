# Debezium (ADR 0020, 0024) : Kafka Connect, image officielle quay.io/debezium/connect copiée dans
# ECR (ADR 0028), en service Fargate. Le connecteur est la configuration invariante de la phase 5
# (deploy/debezium/ledger-outbox.json) fusionnée avec les paramètres d'Aurora : verify-full contre
# les certificats racines RDS, mot de passe par le fournisseur de configuration « env » (secret ECS
# renouvelé tous les 5 jours par la Lambda PKI, qui redéploie le service). Kafka en mTLS avec le
# certificat « debezium » de la PKI interne, installé par la sentinelle au démarrage de la tâche.

data "aws_ec2_managed_prefix_list" "s3" {
  name = "com.amazonaws.${var.region}.s3"
}

locals {
  nom              = "${var.prefixe}-debezium"
  service_actif    = var.version_sentinelle != null
  image_debezium   = "${var.depot_debezium_url}:${var.version_debezium}"
  image_sentinelle = "${var.depot_sentinelle_url}:${coalesce(var.version_sentinelle, "0.0.0")}"

  # Configuration invariante (jamais templatefile : « ${routedByValue} » doit rester littéral).
  connecteur_invariant = jsondecode(file("${path.module}/../../../deploy/debezium/ledger-outbox.json"))
  parametres_connecteur = {
    "database.hostname"    = var.hote_aurora
    "database.port"        = "5432"
    "database.dbname"      = var.nom_base
    "database.user"        = "debezium"
    "database.password"    = "$${env:LEDGER_DEBEZIUM_PASSWORD}"
    "database.sslmode"     = "verify-full"
    "database.sslrootcert" = "/certificats/rds-ca.pem"
  }
  connecteur = merge(local.connecteur_invariant, local.parametres_connecteur)

  # Client Kafka en mTLS : réglages du worker, de ses producteurs (connecteurs source) et consommateurs.
  tls_kafka = merge([
    for prefixe in ["CONNECT_", "CONNECT_PRODUCER_", "CONNECT_CONSUMER_"] : {
      "${prefixe}SECURITY_PROTOCOL"       = "SSL"
      "${prefixe}SSL_KEYSTORE_TYPE"       = "PEM"
      "${prefixe}SSL_KEYSTORE_LOCATION"   = "/certificats/keystore.pem"
      "${prefixe}SSL_TRUSTSTORE_TYPE"     = "PEM"
      "${prefixe}SSL_TRUSTSTORE_LOCATION" = "/certificats/truststore.pem"
      "${prefixe}SSL_PROTOCOL"            = "TLSv1.3"
      "${prefixe}SSL_ENABLED_PROTOCOLS"   = "TLSv1.3"
    }
  ]...)

  environnement_connect = merge(local.tls_kafka, {
    BOOTSTRAP_SERVERS                        = var.serveurs_kafka
    GROUP_ID                                 = "ledger-connect"
    CONFIG_STORAGE_TOPIC                     = "ledger-connect-configs"
    OFFSET_STORAGE_TOPIC                     = "ledger-connect-offsets"
    STATUS_STORAGE_TOPIC                     = "ledger-connect-status"
    CONFIG_STORAGE_REPLICATION_FACTOR        = "3"
    OFFSET_STORAGE_REPLICATION_FACTOR        = "3"
    STATUS_STORAGE_REPLICATION_FACTOR        = "3"
    KAFKA_HEAP_OPTS                          = "-Xms512m -Xmx1g"
    CONNECT_SCHEDULED_REBALANCE_MAX_DELAY_MS = "10000"
    CONNECT_CONFIG_PROVIDERS                 = "env"
    CONNECT_CONFIG_PROVIDERS_ENV_CLASS       = "org.apache.kafka.common.config.provider.EnvVarConfigProvider"
    LEDGER_RDS_CA                            = var.certificat_racine_rds_pem
  })

  script_enregistrement = <<-PY
    import os, time, urllib.error, urllib.request
    corps = os.environ["LEDGER_CONNECTEUR"].encode("utf-8")
    requete = urllib.request.Request("http://localhost:8083/connectors/ledger-outbox/config", data=corps, method="PUT", headers={"Content-Type": "application/json"})
    for _ in range(120):
        try:
            with urllib.request.urlopen(requete, timeout=30) as reponse:
                print(f"Connecteur ledger-outbox enregistré (HTTP {reponse.status}).")
                break
        except urllib.error.HTTPError as erreur:
            if erreur.code != 409:
                raise SystemExit(f"Échec de l'enregistrement : HTTP {erreur.code} {erreur.read().decode('utf-8', 'replace')}")
        except OSError:
            pass
        time.sleep(2)
    else:
        raise SystemExit("Kafka Connect injoignable.")
  PY

  environnement_sentinelle = [
    { name = "Ledger__Sentinelle__Secret", value = var.secret_certificat },
    { name = "Ledger__Sentinelle__Dossier", value = "/certificats" },
    { name = "Ledger__Sentinelle__Serveurs", value = var.serveurs_kafka },
    { name = "Ledger__Sentinelle__Noeud", value = "debezium" },
    { name = "DOTNET_EnableDiagnostics", value = "0" },
  ]

  journaux = {
    for c in ["certificats", "connect", "enregistrement"] : c => {
      logDriver = "awslogs"
      options = {
        "awslogs-group"         = aws_cloudwatch_log_group.debezium.name
        "awslogs-region"        = var.region
        "awslogs-stream-prefix" = c
      }
    }
  }
}

resource "aws_cloudwatch_log_group" "debezium" {
  name              = "/${var.prefixe}/debezium"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

# --- Réseau --------------------------------------------------------------------------------

resource "aws_security_group" "debezium" {
  name        = local.nom
  description = "Kafka Connect : sortie vers Aurora (5432), Kafka (mTLS) et les points de terminaison"
  vpc_id      = var.vpc_id

  tags = {
    Name = local.nom
  }
}

# Workers du même groupe (pendant un déploiement) : relais des requêtes REST vers le chef.
resource "aws_vpc_security_group_ingress_rule" "entre_workers" {
  security_group_id            = aws_security_group.debezium.id
  description                  = "REST Kafka Connect entre workers du groupe"
  ip_protocol                  = "tcp"
  from_port                    = 8083
  to_port                      = 8083
  referenced_security_group_id = aws_security_group.debezium.id
}

resource "aws_vpc_security_group_egress_rule" "entre_workers" {
  security_group_id            = aws_security_group.debezium.id
  description                  = "REST Kafka Connect entre workers du groupe"
  ip_protocol                  = "tcp"
  from_port                    = 8083
  to_port                      = 8083
  referenced_security_group_id = aws_security_group.debezium.id
}

resource "aws_vpc_security_group_egress_rule" "vers_aurora" {
  security_group_id            = aws_security_group.debezium.id
  description                  = "PostgreSQL (TLS verify-full) vers le redacteur Aurora"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = var.groupe_securite_aurora
}

resource "aws_vpc_security_group_egress_rule" "vers_kafka" {
  security_group_id            = aws_security_group.debezium.id
  description                  = "Kafka en mTLS"
  ip_protocol                  = "tcp"
  from_port                    = var.port_kafka
  to_port                      = var.port_kafka
  referenced_security_group_id = var.groupe_securite_kafka
}

resource "aws_vpc_security_group_egress_rule" "vers_points_de_terminaison" {
  security_group_id            = aws_security_group.debezium.id
  description                  = "HTTPS vers les points de terminaison VPC"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = var.groupe_securite_points_de_terminaison
}

resource "aws_vpc_security_group_egress_rule" "vers_s3" {
  security_group_id = aws_security_group.debezium.id
  description       = "HTTPS vers S3 (couches des images ECR)"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  prefix_list_id    = data.aws_ec2_managed_prefix_list.s3.id
}

resource "aws_vpc_security_group_ingress_rule" "aurora_depuis_debezium" {
  security_group_id            = var.groupe_securite_aurora
  description                  = "PostgreSQL depuis Debezium"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = aws_security_group.debezium.id
}

resource "aws_vpc_security_group_ingress_rule" "kafka_depuis_debezium" {
  security_group_id            = var.groupe_securite_kafka
  description                  = "mTLS depuis Debezium"
  ip_protocol                  = "tcp"
  from_port                    = var.port_kafka
  to_port                      = var.port_kafka
  referenced_security_group_id = aws_security_group.debezium.id
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
  name                 = "${local.nom}-execution"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn
  assume_role_policy   = local.confiance_taches
}

resource "aws_iam_role_policy" "execution" {
  name = "execution-debezium"
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
        Sid      = "TirageImages"
        Effect   = "Allow"
        Action   = ["ecr:BatchCheckLayerAvailability", "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer"]
        Resource = [var.depot_debezium_arn, var.depot_sentinelle_arn]
      },
      {
        Sid      = "Journaux"
        Effect   = "Allow"
        Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
        Resource = "${aws_cloudwatch_log_group.debezium.arn}:*"
      },
      {
        Sid      = "MotDePassePostgres"
        Effect   = "Allow"
        Action   = "secretsmanager:GetSecretValue"
        Resource = var.secret_mot_de_passe
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

# Rôle de la tâche : la sentinelle lit le certificat client « debezium ».
resource "aws_iam_role" "tache" {
  name                 = "${local.nom}-tache"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn
  assume_role_policy   = local.confiance_taches
}

resource "aws_iam_role_policy" "tache" {
  name = "certificat-debezium"
  role = aws_iam_role.tache.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "CertificatClient"
        Effect   = "Allow"
        Action   = ["secretsmanager:DescribeSecret", "secretsmanager:GetSecretValue"]
        Resource = var.secret_certificat
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

# --- Tâche et service ----------------------------------------------------------------------

resource "aws_ecs_task_definition" "debezium" {
  #checkov:skip=CKV_AWS_336:Le script de l'image Debezium écrit la configuration du worker (/kafka/config) au démarrage : système de fichiers racine en écriture pour Kafka Connect seulement ; la sentinelle et l'enregistrement sont en lecture seule.
  count = local.service_actif ? 1 : 0

  family                   = local.nom
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 1024
  memory                   = 2048
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.tache.arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  volume {
    name = "certificats"
  }

  container_definitions = jsonencode([
    {
      name                   = "certificats"
      image                  = local.image_sentinelle
      essential              = false
      user                   = "1001"
      readonlyRootFilesystem = true
      command                = ["installer"]
      environment            = local.environnement_sentinelle
      mountPoints            = [{ sourceVolume = "certificats", containerPath = "/certificats", readOnly = false }]
      logConfiguration       = local.journaux["certificats"]
    },
    {
      name        = "connect"
      image       = local.image_debezium
      essential   = true
      user        = "1001"
      entryPoint  = ["sh", "-c"]
      command     = ["printf '%s' \"$LEDGER_RDS_CA\" > /certificats/rds-ca.pem && exec /docker-entrypoint.sh start"]
      environment = [for nom in sort(keys(local.environnement_connect)) : { name = nom, value = local.environnement_connect[nom] }]
      secrets = [
        { name = "LEDGER_DEBEZIUM_PASSWORD", valueFrom = "${var.secret_mot_de_passe}:mot_de_passe::" },
      ]
      portMappings = [{ containerPort = 8083, protocol = "tcp" }]
      mountPoints  = [{ sourceVolume = "certificats", containerPath = "/certificats", readOnly = false }]
      dependsOn    = [{ containerName = "certificats", condition = "SUCCESS" }]
      healthCheck = {
        command     = ["CMD-SHELL", "curl -fs http://localhost:8083/ >/dev/null || exit 1"]
        interval    = 15
        timeout     = 5
        retries     = 10
        startPeriod = 60
      }
      logConfiguration = local.journaux["connect"]
    },
    {
      name                   = "enregistrement"
      image                  = local.image_debezium
      essential              = false
      user                   = "1001"
      readonlyRootFilesystem = true
      entryPoint             = ["python3", "-c"]
      command                = [local.script_enregistrement]
      environment            = [{ name = "LEDGER_CONNECTEUR", value = jsonencode(local.connecteur) }]
      dependsOn              = [{ containerName = "connect", condition = "HEALTHY" }]
      logConfiguration       = local.journaux["enregistrement"]
    },
  ])

  lifecycle {
    precondition {
      condition     = length(setintersection(keys(local.connecteur_invariant), keys(local.parametres_connecteur))) == 0
      error_message = "Un paramètre d'environnement ne peut pas redéfinir une clé invariante du connecteur (ADR 0020)."
    }
  }
}

resource "aws_ecs_service" "debezium" {
  count = local.service_actif ? 1 : 0

  name            = "debezium"
  cluster         = var.arn_grappe
  task_definition = aws_ecs_task_definition.debezium[0].arn
  desired_count   = 1
  launch_type     = "FARGATE"

  # Le nouveau worker rejoint le groupe avant l'arrêt de l'ancien : le connecteur est réaffecté.
  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  network_configuration {
    subnets          = var.sous_reseaux
    security_groups  = [aws_security_group.debezium.id]
    assign_public_ip = false
  }
}

# --- État du connecteur et de sa tâche (journaux → métrique → alarme, sans action) ----------

resource "aws_cloudwatch_log_metric_filter" "echec_connecteur" {
  name           = "${local.nom}-echec-connecteur"
  log_group_name = aws_cloudwatch_log_group.debezium.name
  pattern        = "?\"Task threw an uncaught and unrecoverable exception\" ?\"is being killed and will not recover\" ?\"Failed to start connector\""

  metric_transformation {
    name          = "EchecsConnecteur"
    namespace     = "Ledger/Cdc"
    value         = "1"
    default_value = "0"
  }
}

resource "aws_cloudwatch_metric_alarm" "echec_connecteur" {
  alarm_name          = "${local.nom}-echec-connecteur"
  alarm_description   = "Le connecteur ledger-outbox ou sa tache est en echec (journaux de Kafka Connect)."
  namespace           = "Ledger/Cdc"
  metric_name         = "EchecsConnecteur"
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  comparison_operator = "GreaterThanThreshold"
  threshold           = 0
  treat_missing_data  = "notBreaching"
}

resource "aws_cloudwatch_metric_alarm" "service_arrete" {
  count = local.service_actif ? 1 : 0

  alarm_name          = "${local.nom}-service-arrete"
  alarm_description   = "Aucune tache Kafka Connect en cours d execution."
  namespace           = "ECS/ContainerInsights"
  metric_name         = "RunningTaskCount"
  dimensions          = { ClusterName = element(split("/", var.arn_grappe), 1), ServiceName = "debezium" }
  statistic           = "Minimum"
  period              = 300
  evaluation_periods  = 2
  comparison_operator = "LessThanThreshold"
  threshold           = 1
  treat_missing_data  = "breaching"
}
