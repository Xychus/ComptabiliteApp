# Keycloak (ADR 0030) : émetteur des jetons du grand livre, image officielle quay.io/keycloak/keycloak
# copiée dans ECR (ADR 0028), en service Fargate dans le VPC, sans sortie Internet. HTTPS sur 8443
# par le certificat serveur « keycloak » de la PKI interne, installé par la sentinelle au démarrage
# (keystore.pem : clé puis chaîne, lu comme certificat et comme clé ; truststore.pem : racine),
# TLS 1.3 seulement, mTLS demandé : les clients internes s'authentifient par leur certificat de la
# PKI. Realm importé au démarrage (infra/keycloak/ledger-realm.json), aucun compte d'administration.
# Base : schéma « keycloak » d'Aurora (migration 0010), verify-full, mot de passe renouvelé tous
# les 5 jours par la Lambda PKI, qui redéploie le service (comme après le renouvellement du certificat).

data "aws_ec2_managed_prefix_list" "s3" {
  name = "com.amazonaws.${var.region}.s3"
}

locals {
  nom              = "${var.prefixe}-keycloak"
  service_actif    = var.version_sentinelle != null
  domaine          = "idp.${var.domaine_interne}"
  nom_hote         = "keycloak.${local.domaine}"
  port_https       = 8443
  port_gestion     = 9000
  image_keycloak   = "${var.depot_keycloak_url}:${var.version_keycloak}"
  image_sentinelle = "${var.depot_sentinelle_url}:${coalesce(var.version_sentinelle, "0.0.0")}"

  # Réimprimé sans espaces : variable d'environnement de la tâche, écrite en fichier au démarrage.
  realm = jsonencode(jsondecode(file("${path.module}/../../keycloak/ledger-realm.json")))

  environnement_keycloak = {
    KC_DB                         = "postgres"
    KC_DB_URL                     = "jdbc:postgresql://${var.hote_aurora}:5432/${var.nom_base}?sslmode=verify-full&sslrootcert=/certificats/rds-ca.pem"
    KC_DB_USERNAME                = "keycloak"
    KC_DB_SCHEMA                  = "keycloak"
    KC_HOSTNAME                   = "https://${local.nom_hote}:${local.port_https}"
    KC_HTTP_ENABLED               = "false"
    KC_HTTPS_PORT                 = tostring(local.port_https)
    KC_HTTPS_CERTIFICATE_FILE     = "/certificats/keystore.pem"
    KC_HTTPS_CERTIFICATE_KEY_FILE = "/certificats/keystore.pem"
    KC_HTTPS_TRUST_STORE_FILE     = "/certificats/truststore.pem"
    KC_HTTPS_CLIENT_AUTH          = "request"
    # TLS 1.3 seulement : clients .NET 10 (API, autorizer, applications internes) uniquement.
    KC_HTTPS_PROTOCOLS    = "TLSv1.3"
    KC_HEALTH_ENABLED     = "true"
    KC_LOG_CONSOLE_OUTPUT = "json"
    JAVA_OPTS_KC_HEAP     = "-XX:MaxRAMPercentage=70"
    LEDGER_REALM          = local.realm
    LEDGER_RDS_CA         = var.certificat_racine_rds_pem
  }

  demarrage = join(" && ", [
    "mkdir -p /opt/keycloak/data/import",
    "printf '%s' \"$LEDGER_REALM\" > /opt/keycloak/data/import/ledger-realm.json",
    "printf '%s' \"$LEDGER_RDS_CA\" > /certificats/rds-ca.pem",
    "exec /opt/keycloak/bin/kc.sh start --import-realm",
  ])

  environnement_sentinelle = [
    { name = "Ledger__Sentinelle__Secret", value = var.secret_certificat },
    { name = "Ledger__Sentinelle__Dossier", value = "/certificats" },
    { name = "Ledger__Sentinelle__Serveurs", value = var.serveurs_kafka },
    { name = "Ledger__Sentinelle__Noeud", value = "keycloak" },
    { name = "DOTNET_EnableDiagnostics", value = "0" },
  ]

  journaux = {
    for c in ["certificats", "keycloak"] : c => {
      logDriver = "awslogs"
      options = {
        "awslogs-group"         = aws_cloudwatch_log_group.keycloak.name
        "awslogs-region"        = var.region
        "awslogs-stream-prefix" = c
      }
    }
  }
}

resource "aws_cloudwatch_log_group" "keycloak" {
  name              = "/${var.prefixe}/keycloak"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

# --- Réseau --------------------------------------------------------------------------------

resource "aws_security_group" "keycloak" {
  name        = local.nom
  description = "Keycloak : HTTPS (mTLS) depuis le VPC, grappe entre taches, sortie Aurora et points de terminaison"
  vpc_id      = var.vpc_id

  tags = {
    Name = local.nom
  }
}

resource "aws_vpc_security_group_ingress_rule" "https_depuis_vpc" {
  security_group_id = aws_security_group.keycloak.id
  description       = "HTTPS (mTLS) depuis le VPC : API, autorizer, applications internes"
  ip_protocol       = "tcp"
  from_port         = local.port_https
  to_port           = local.port_https
  cidr_ipv4         = var.cidr_vpc
}

# Grappe Infinispan (pile jdbc-ping) : transport 7800 et détection des pannes 57800.
resource "aws_vpc_security_group_ingress_rule" "grappe" {
  for_each = toset(["7800", "57800"])

  security_group_id            = aws_security_group.keycloak.id
  description                  = "Grappe Keycloak entre taches (${each.key})"
  ip_protocol                  = "tcp"
  from_port                    = tonumber(each.key)
  to_port                      = tonumber(each.key)
  referenced_security_group_id = aws_security_group.keycloak.id
}

resource "aws_vpc_security_group_egress_rule" "grappe" {
  for_each = toset(["7800", "57800"])

  security_group_id            = aws_security_group.keycloak.id
  description                  = "Grappe Keycloak entre taches (${each.key})"
  ip_protocol                  = "tcp"
  from_port                    = tonumber(each.key)
  to_port                      = tonumber(each.key)
  referenced_security_group_id = aws_security_group.keycloak.id
}

resource "aws_vpc_security_group_egress_rule" "vers_aurora" {
  security_group_id            = aws_security_group.keycloak.id
  description                  = "PostgreSQL (TLS verify-full) vers le redacteur Aurora"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = var.groupe_securite_aurora
}

resource "aws_vpc_security_group_egress_rule" "vers_points_de_terminaison" {
  security_group_id            = aws_security_group.keycloak.id
  description                  = "HTTPS vers les points de terminaison VPC"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = var.groupe_securite_points_de_terminaison
}

resource "aws_vpc_security_group_egress_rule" "vers_s3" {
  security_group_id = aws_security_group.keycloak.id
  description       = "HTTPS vers S3 (couches des images ECR)"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  prefix_list_id    = data.aws_ec2_managed_prefix_list.s3.id
}

resource "aws_vpc_security_group_ingress_rule" "aurora_depuis_keycloak" {
  security_group_id            = var.groupe_securite_aurora
  description                  = "PostgreSQL depuis Keycloak"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = aws_security_group.keycloak.id
}

# --- Découverte (Cloud Map, DNS privé) -----------------------------------------------------

resource "aws_service_discovery_private_dns_namespace" "idp" {
  name        = local.domaine
  description = "Fournisseur d'identite de ${var.prefixe}"
  vpc         = var.vpc_id
}

resource "aws_service_discovery_service" "keycloak" {
  name = "keycloak"

  dns_config {
    namespace_id   = aws_service_discovery_private_dns_namespace.idp.id
    routing_policy = "MULTIVALUE"

    dns_records {
      type = "A"
      ttl  = 10
    }
  }
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

  dechiffrement_secret = {
    Sid       = "DechiffrementSecret"
    Effect    = "Allow"
    Action    = "kms:Decrypt"
    Resource  = var.cle_kms_secrets_arn
    Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.${var.region}.amazonaws.com" } }
  }
}

resource "aws_iam_role" "execution" {
  name                 = "${local.nom}-execution"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn
  assume_role_policy   = local.confiance_taches
}

resource "aws_iam_role_policy" "execution" {
  name = "execution-keycloak"
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
        Resource = [var.depot_keycloak_arn, var.depot_sentinelle_arn]
      },
      {
        Sid      = "Journaux"
        Effect   = "Allow"
        Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
        Resource = "${aws_cloudwatch_log_group.keycloak.arn}:*"
      },
      {
        Sid      = "MotDePassePostgres"
        Effect   = "Allow"
        Action   = "secretsmanager:GetSecretValue"
        Resource = var.secret_mot_de_passe
      },
      local.dechiffrement_secret,
    ]
  })
}

# Rôle de la tâche : la sentinelle lit le certificat serveur « keycloak ». Keycloak lui-même
# n'appelle aucun service AWS.
resource "aws_iam_role" "tache" {
  name                 = "${local.nom}-tache"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn
  assume_role_policy   = local.confiance_taches
}

resource "aws_iam_role_policy" "tache" {
  name = "certificat-keycloak"
  role = aws_iam_role.tache.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "CertificatServeur"
        Effect   = "Allow"
        Action   = ["secretsmanager:DescribeSecret", "secretsmanager:GetSecretValue"]
        Resource = var.secret_certificat
      },
      local.dechiffrement_secret,
    ]
  })
}

# --- Tâche et service ----------------------------------------------------------------------

resource "aws_ecs_task_definition" "keycloak" {
  #checkov:skip=CKV_AWS_336:Keycloak reconstruit sa distribution au démarrage (kc.sh start, /opt/keycloak/lib/quarkus) et écrit le realm importé : système de fichiers racine en écriture pour Keycloak seulement ; la sentinelle est en lecture seule.
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
      name = "keycloak"
      # Même utilisateur que la sentinelle (fichiers en 0600), groupe root : l'image officielle est
      # prévue pour un uid arbitraire du groupe 0 (/opt/keycloak inscriptible par le groupe).
      image       = local.image_keycloak
      essential   = true
      user        = "1001:0"
      entryPoint  = ["/bin/bash", "-c"]
      command     = [local.demarrage]
      environment = [for nom in sort(keys(local.environnement_keycloak)) : { name = nom, value = local.environnement_keycloak[nom] }]
      secrets = [
        { name = "KC_DB_PASSWORD", valueFrom = "${var.secret_mot_de_passe}:mot_de_passe::" },
      ]
      portMappings = [
        { containerPort = local.port_https, protocol = "tcp" },
        { containerPort = 7800, protocol = "tcp" },
        { containerPort = 57800, protocol = "tcp" },
      ]
      mountPoints = [{ sourceVolume = "certificats", containerPath = "/certificats", readOnly = false }]
      dependsOn   = [{ containerName = "certificats", condition = "SUCCESS" }]
      # Ni curl ni wget dans l'image : ouverture TCP de l'interface de gestion par bash.
      healthCheck = {
        command     = ["CMD-SHELL", "exec 3<>/dev/tcp/127.0.0.1/${local.port_gestion} || exit 1"]
        interval    = 15
        timeout     = 5
        retries     = 5
        startPeriod = 180
      }
      logConfiguration = local.journaux["keycloak"]
    },
  ])
}

resource "aws_ecs_service" "keycloak" {
  count = local.service_actif ? 1 : 0

  name            = "keycloak"
  cluster         = var.arn_grappe
  task_definition = aws_ecs_task_definition.keycloak[0].arn
  desired_count   = var.nombre_taches
  launch_type     = "FARGATE"

  # Nouvelle tâche saine avant l'arrêt de l'ancienne (renouvellement du certificat ou du mot de passe).
  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  network_configuration {
    subnets          = var.sous_reseaux
    security_groups  = [aws_security_group.keycloak.id]
    assign_public_ip = false
  }

  service_registries {
    registry_arn = aws_service_discovery_service.keycloak.arn
  }
}

resource "aws_cloudwatch_metric_alarm" "service_arrete" {
  count = local.service_actif ? 1 : 0

  alarm_name          = "${local.nom}-service-arrete"
  alarm_description   = "Aucune tache Keycloak en cours d execution : plus aucun jeton emis."
  namespace           = "ECS/ContainerInsights"
  metric_name         = "RunningTaskCount"
  dimensions          = { ClusterName = element(split("/", var.arn_grappe), 1), ServiceName = "keycloak" }
  statistic           = "Minimum"
  period              = 300
  evaluation_periods  = 2
  comparison_operator = "LessThanThreshold"
  threshold           = 1
  treat_missing_data  = "breaching"
}
