# Kafka autogéré (ADR 0024, révisée par l'ADR 0028) : image officielle apache/kafka en KRaft,
# trois nœuds combinés contrôleur + broker, un par zone, sur ECS (lancement EC2). Chaque nœud a :
#   - son instance EC2 (aws_instance, pas de groupe Auto Scaling) et son volume EBS persistant
#     chiffré par la CMK « kafka », rattaché par Terraform : il survit au remplacement de la tâche ;
#   - son service ECS (une tâche, réseau awsvpc, IMDS bloqué pour la tâche) et son nom stable
#     kafka-N.kafka.<domaine> publié par Cloud Map (découverte seulement, pas de Service Connect) ;
#   - sa sentinelle (Ledger.Pki.Sentinelle) : installe le certificat de 7 jours, redémarre le nœud
#     dans son créneau après une rotation, publie les mesures de la grappe (Ledger/Kafka).
# Écouteurs SSL seulement (MTLS client et inter-broker, CONTROLLER), mTLS exigé, TLS 1.3 seul,
# ACL par identité de certificat, refus par défaut.

data "aws_ssm_parameter" "ami_ecs" {
  name = "/aws/service/ecs/optimized-ami/amazon-linux-2023/recommended/image_id"
}

data "aws_ec2_managed_prefix_list" "s3" {
  name = "com.amazonaws.${var.region}.s3"
}

data "aws_subnet" "noeud" {
  for_each = local.noeuds

  id = each.value.sous_reseau
}

locals {
  nom              = "${var.prefixe}-kafka"
  domaine_kafka    = "kafka.${var.domaine_interne}"
  port_client      = 9093
  port_controleur  = 9094
  services_actifs  = var.version_sentinelle != null
  image_kafka      = "${var.depot_kafka_url}:${var.version_kafka}"
  image_sentinelle = "${var.depot_sentinelle_url}:${coalesce(var.version_sentinelle, "0.0.0")}"

  noeuds = {
    for i in range(3) : "kafka-${i + 1}" => {
      id          = i + 1
      creneau     = i
      sous_reseau = var.sous_reseaux[i]
      dns         = "kafka-${i + 1}.${local.domaine_kafka}"
    }
  }

  serveurs           = join(",", [for n in values(local.noeuds) : "${n.dns}:${local.port_client}"])
  votants            = join(",", [for n in values(local.noeuds) : "${n.id}@${n.dns}:${local.port_controleur}"])
  super_utilisateurs = join(";", concat([for nom, _ in local.noeuds : "User:${nom}"], ["User:kafka-admin"]))

  environnement_kafka = {
    for nom, n in local.noeuds : nom => [
      { name = "CLUSTER_ID", value = var.identifiant_grappe_kraft },
      { name = "KAFKA_NODE_ID", value = tostring(n.id) },
      { name = "KAFKA_PROCESS_ROLES", value = "broker,controller" },
      { name = "KAFKA_CONTROLLER_QUORUM_VOTERS", value = local.votants },
      # L'écouteur ne s'appelle pas « SSL » : sur « SSL:// », le script de l'image exige des keystores JKS.
      { name = "KAFKA_LISTENERS", value = "MTLS://0.0.0.0:${local.port_client},CONTROLLER://0.0.0.0:${local.port_controleur}" },
      { name = "KAFKA_ADVERTISED_LISTENERS", value = "MTLS://${n.dns}:${local.port_client}" },
      { name = "KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", value = "MTLS:SSL,CONTROLLER:SSL" },
      { name = "KAFKA_INTER_BROKER_LISTENER_NAME", value = "MTLS" },
      { name = "KAFKA_CONTROLLER_LISTENER_NAMES", value = "CONTROLLER" },
      { name = "KAFKA_SSL_KEYSTORE_TYPE", value = "PEM" },
      { name = "KAFKA_SSL_KEYSTORE_LOCATION", value = "/certificats/keystore.pem" },
      { name = "KAFKA_SSL_TRUSTSTORE_TYPE", value = "PEM" },
      { name = "KAFKA_SSL_TRUSTSTORE_LOCATION", value = "/certificats/truststore.pem" },
      { name = "KAFKA_SSL_CLIENT_AUTH", value = "required" },
      # TLS 1.3 seul : tous les clients le prennent en charge (Java 21, librdkafka avec OpenSSL 3).
      { name = "KAFKA_SSL_PROTOCOL", value = "TLSv1.3" },
      { name = "KAFKA_SSL_ENABLED_PROTOCOLS", value = "TLSv1.3" },
      { name = "KAFKA_SSL_PRINCIPAL_MAPPING_RULES", value = "RULE:^CN=([a-z0-9-]+)$/$1/,DEFAULT" },
      { name = "KAFKA_AUTHORIZER_CLASS_NAME", value = "org.apache.kafka.metadata.authorizer.StandardAuthorizer" },
      { name = "KAFKA_SUPER_USERS", value = local.super_utilisateurs },
      { name = "KAFKA_ALLOW_EVERYONE_IF_NO_ACL_FOUND", value = "false" },
      { name = "KAFKA_AUTO_CREATE_TOPICS_ENABLE", value = "false" },
      { name = "KAFKA_DEFAULT_REPLICATION_FACTOR", value = "3" },
      { name = "KAFKA_MIN_INSYNC_REPLICAS", value = "2" },
      { name = "KAFKA_UNCLEAN_LEADER_ELECTION_ENABLE", value = "false" },
      { name = "KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR", value = "3" },
      { name = "KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR", value = "3" },
      { name = "KAFKA_TRANSACTION_STATE_LOG_MIN_ISR", value = "2" },
      { name = "KAFKA_LOG_DIRS", value = "/var/lib/kafka/data" },
      { name = "KAFKA_HEAP_OPTS", value = "-Xms${floor(var.memoire_kafka_mio / 2)}m -Xmx${floor(var.memoire_kafka_mio / 2)}m" },
    ]
  }

  # Commandes de la tâche d'initialisation (idempotentes) : sujet des événements et ACL.
  commandes_acl = flatten([
    for identite, droits in var.acl : concat(
      [for s in droits.sujets_ecriture : "kafka-acls.sh $A --allow-principal User:${identite} --operation Write --operation Describe --topic ${s}"],
      length(droits.sujets_ecriture) > 0 ? ["kafka-acls.sh $A --allow-principal User:${identite} --operation IdempotentWrite --cluster"] : [],
      [for s in droits.sujets_lecture : "kafka-acls.sh $A --allow-principal User:${identite} --operation Read --operation Describe --topic ${s}"],
      [for p in droits.prefixes_sujets : "kafka-acls.sh $A --allow-principal User:${identite} --resource-pattern-type prefixed --operation Read --operation Write --operation Describe --operation DescribeConfigs --operation Create --topic ${p}"],
      [for g in droits.groupes : "kafka-acls.sh $A --allow-principal User:${identite} --operation Read --group ${g}"],
    )
  ])

  script_initialisation = join("\n", concat(
    [
      "set -eu",
      "export PATH=/opt/kafka/bin:$PATH LOG_DIR=/tmp",
      "C=/tmp/client.properties",
      "printf 'security.protocol=SSL\\nssl.keystore.type=PEM\\nssl.keystore.location=/certificats/keystore.pem\\nssl.truststore.type=PEM\\nssl.truststore.location=/certificats/truststore.pem\\nssl.enabled.protocols=TLSv1.3\\n' > $C",
      "B=${local.serveurs}",
      "A=\"--bootstrap-server $B --command-config $C --add\"",
      "kafka-topics.sh --bootstrap-server $B --command-config $C --create --if-not-exists --topic ledger.events --partitions ${var.partitions_evenements} --replication-factor 3 --config min.insync.replicas=2",
    ],
    local.commandes_acl,
    ["kafka-acls.sh --bootstrap-server $B --command-config $C --list"],
  ))
}

# --- Réseau --------------------------------------------------------------------------------

resource "aws_security_group" "noeuds" {
  name        = local.nom
  description = "Taches Kafka : ecouteurs MTLS (9093) et CONTROLLER (9094) en mTLS"
  vpc_id      = var.vpc_id

  tags = {
    Name = local.nom
  }
}

resource "aws_vpc_security_group_ingress_rule" "entre_noeuds" {
  for_each = toset([tostring(local.port_client), tostring(local.port_controleur)])

  security_group_id            = aws_security_group.noeuds.id
  description                  = "Inter-broker et quorum KRaft (port ${each.key})"
  ip_protocol                  = "tcp"
  from_port                    = tonumber(each.key)
  to_port                      = tonumber(each.key)
  referenced_security_group_id = aws_security_group.noeuds.id
}

resource "aws_vpc_security_group_egress_rule" "vers_noeuds" {
  for_each = toset([tostring(local.port_client), tostring(local.port_controleur)])

  security_group_id            = aws_security_group.noeuds.id
  description                  = "Inter-broker et quorum KRaft (port ${each.key})"
  ip_protocol                  = "tcp"
  from_port                    = tonumber(each.key)
  to_port                      = tonumber(each.key)
  referenced_security_group_id = aws_security_group.noeuds.id
}

resource "aws_vpc_security_group_egress_rule" "noeuds_vers_points_de_terminaison" {
  security_group_id            = aws_security_group.noeuds.id
  description                  = "HTTPS vers les points de terminaison VPC (Secrets Manager, CloudWatch)"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = var.groupe_securite_points_de_terminaison
}

# Instances : agent ECS et tirage des images seulement (aucune entrée).
resource "aws_security_group" "instances" {
  name        = "${local.nom}-instances"
  description = "Instances hotes des noeuds Kafka : sortie HTTPS vers les points de terminaison et S3"
  vpc_id      = var.vpc_id

  tags = {
    Name = "${local.nom}-instances"
  }
}

resource "aws_vpc_security_group_egress_rule" "instances_vers_points_de_terminaison" {
  security_group_id            = aws_security_group.instances.id
  description                  = "HTTPS vers les points de terminaison VPC (ECS, ECR, Logs, SSM)"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = var.groupe_securite_points_de_terminaison
}

resource "aws_vpc_security_group_egress_rule" "instances_vers_s3" {
  security_group_id = aws_security_group.instances.id
  description       = "HTTPS vers S3 (couches des images ECR)"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  prefix_list_id    = data.aws_ec2_managed_prefix_list.s3.id
}

# --- Découverte (Cloud Map, DNS privé) -----------------------------------------------------

resource "aws_service_discovery_private_dns_namespace" "kafka" {
  name        = local.domaine_kafka
  description = "Noms stables des noeuds Kafka de ${var.prefixe}"
  vpc         = var.vpc_id
}

resource "aws_service_discovery_service" "noeud" {
  for_each = local.noeuds

  name = each.key

  dns_config {
    namespace_id   = aws_service_discovery_private_dns_namespace.kafka.id
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
}

# Instances : agent ECS de la grappe seulement (les images sont tirées par le rôle d'exécution).
resource "aws_iam_role" "instance" {
  name                 = "${local.nom}-instance"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "ec2.amazonaws.com" }
      Action    = "sts:AssumeRole"
      Condition = { StringEquals = { "aws:SourceAccount" = var.compte_aws } }
    }]
  })
}

resource "aws_iam_role_policy" "instance" {
  name = "agent-ecs"
  role = aws_iam_role.instance.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "AgentGrappe"
        Effect = "Allow"
        Action = [
          "ecs:DeregisterContainerInstance", "ecs:Poll", "ecs:RegisterContainerInstance", "ecs:StartTelemetrySession",
          "ecs:SubmitAttachmentStateChange", "ecs:SubmitContainerStateChange", "ecs:SubmitTaskStateChange",
          "ecs:TagResource", "ecs:UpdateContainerInstancesState",
        ]
        Resource = [var.arn_grappe, "arn:aws:ecs:${var.region}:${var.compte_aws}:container-instance/${var.nom_grappe}/*"]
      },
      # Seule action de l'agent sans ressource précise (documentation ECS).
      {
        Sid      = "AgentPointDeSondage"
        Effect   = "Allow"
        Action   = "ecs:DiscoverPollEndpoint"
        Resource = "*"
      },
    ]
  })
}

resource "aws_iam_instance_profile" "instance" {
  name = "${local.nom}-instance"
  path = "/ledger/"
  role = aws_iam_role.instance.name
}

resource "aws_iam_role" "execution" {
  name                 = "${local.nom}-execution"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn
  assume_role_policy   = local.confiance_taches
}

resource "aws_iam_role_policy" "execution" {
  name = "execution-kafka"
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
        Resource = [var.depot_kafka_arn, var.depot_sentinelle_arn]
      },
      {
        Sid      = "Journaux"
        Effect   = "Allow"
        Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
        Resource = "${aws_cloudwatch_log_group.kafka.arn}:*"
      },
    ]
  })
}

# Un rôle de tâche par nœud : sa sentinelle ne lit que le secret de son nœud.
resource "aws_iam_role" "sentinelle" {
  for_each = merge(var.secrets_noeuds, { "kafka-admin" = var.secret_administration })

  name                 = "${var.prefixe}-${each.key}-sentinelle"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn
  assume_role_policy   = local.confiance_taches
}

resource "aws_iam_role_policy" "sentinelle" {
  for_each = aws_iam_role.sentinelle

  name = "sentinelle"
  role = each.value.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "CertificatDuNoeud"
        Effect   = "Allow"
        Action   = ["secretsmanager:DescribeSecret", "secretsmanager:GetSecretValue"]
        Resource = each.key == "kafka-admin" ? var.secret_administration : var.secrets_noeuds[each.key]
      },
      {
        Sid       = "DechiffrementSecret"
        Effect    = "Allow"
        Action    = "kms:Decrypt"
        Resource  = var.cle_kms_secrets_arn
        Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.${var.region}.amazonaws.com" } }
      },
      {
        Sid       = "MesuresGrappe"
        Effect    = "Allow"
        Action    = "cloudwatch:PutMetricData"
        Resource  = "*"
        Condition = { StringEquals = { "cloudwatch:namespace" = "Ledger/Kafka" } }
      },
    ]
  })
}

resource "aws_cloudwatch_log_group" "kafka" {
  name              = "/${var.prefixe}/kafka"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

# --- Instances et volumes ------------------------------------------------------------------

resource "aws_ebs_volume" "donnees" {
  for_each = local.noeuds

  availability_zone = data.aws_subnet.noeud[each.key].availability_zone
  size              = var.taille_volume_go
  type              = "gp3"
  encrypted         = true
  kms_key_id        = var.cle_kms_kafka_arn

  tags = {
    Name  = "${var.prefixe}-${each.key}-donnees"
    noeud = each.key
  }
}

resource "aws_instance" "noeud" {
  for_each = local.noeuds

  ami                    = data.aws_ssm_parameter.ami_ecs.value
  instance_type          = var.type_instance
  subnet_id              = each.value.sous_reseau
  vpc_security_group_ids = [aws_security_group.instances.id]
  iam_instance_profile   = aws_iam_instance_profile.instance.name
  monitoring             = true
  ebs_optimized          = true

  metadata_options {
    http_endpoint               = "enabled"
    http_tokens                 = "required"
    http_put_response_hop_limit = 1
  }

  root_block_device {
    encrypted   = true
    kms_key_id  = var.cle_kms_kafka_arn
    volume_type = "gp3"
  }

  user_data = templatefile("${path.module}/demarrage-noeud.sh.tftpl", {
    grappe    = var.nom_grappe
    noeud     = each.key
    volume_id = replace(aws_ebs_volume.donnees[each.key].id, "-", "")
  })

  tags = {
    Name  = "${var.prefixe}-${each.key}"
    noeud = each.key
  }

  # Remplacer un nœud est une opération manuelle, un nœud à la fois (runbook
  # kafka-remplacement-noeud) : une nouvelle AMI ou un nouveau script ne déclenche rien seul.
  lifecycle {
    ignore_changes = [ami, user_data]
  }
}

resource "aws_volume_attachment" "donnees" {
  for_each = local.noeuds

  device_name = "/dev/sdf"
  volume_id   = aws_ebs_volume.donnees[each.key].id
  instance_id = aws_instance.noeud[each.key].id
}

# --- Tâches et services --------------------------------------------------------------------

resource "aws_ecs_task_definition" "noeud" {
  #checkov:skip=CKV_AWS_336:Le script de l'image apache/kafka écrit server.properties dans /opt/kafka/config au démarrage : système de fichiers racine en écriture pour ce seul conteneur ; la sentinelle et l'initialisation sont en lecture seule.
  for_each = local.services_actifs ? local.noeuds : {}

  family                   = "${var.prefixe}-${each.key}"
  requires_compatibilities = ["EC2"]
  network_mode             = "awsvpc"
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.sentinelle[each.key].arn

  volume {
    name      = "donnees"
    host_path = "/donnees/kafka"
  }

  volume {
    name = "certificats"
  }

  placement_constraints {
    type       = "memberOf"
    expression = "attribute:ledger.noeud == ${each.key}"
  }

  container_definitions = jsonencode([
    {
      name                   = "certificats"
      image                  = local.image_sentinelle
      essential              = false
      user                   = "1000"
      readonlyRootFilesystem = true
      memory                 = 256
      command                = ["installer"]
      environment            = local.environnement_sentinelle[each.key]
      mountPoints            = [{ sourceVolume = "certificats", containerPath = "/certificats", readOnly = false }]
      logConfiguration       = local.journaux["${each.key}-certificats"]
    },
    {
      name        = "kafka"
      image       = local.image_kafka
      essential   = true
      user        = "1000"
      memory      = var.memoire_kafka_mio
      environment = local.environnement_kafka[each.key]
      portMappings = [
        { containerPort = local.port_client, protocol = "tcp" },
        { containerPort = local.port_controleur, protocol = "tcp" },
      ]
      mountPoints = [
        { sourceVolume = "donnees", containerPath = "/var/lib/kafka/data", readOnly = false },
        { sourceVolume = "certificats", containerPath = "/certificats", readOnly = true },
      ]
      dependsOn        = [{ containerName = "certificats", condition = "SUCCESS" }]
      ulimits          = [{ name = "nofile", softLimit = 100000, hardLimit = 100000 }]
      logConfiguration = local.journaux["${each.key}-kafka"]
    },
    {
      name                   = "sentinelle"
      image                  = local.image_sentinelle
      essential              = true
      user                   = "1000"
      readonlyRootFilesystem = true
      memory                 = 256
      environment            = local.environnement_sentinelle[each.key]
      mountPoints = [
        { sourceVolume = "certificats", containerPath = "/certificats", readOnly = true },
        { sourceVolume = "donnees", containerPath = "/donnees", readOnly = true },
      ]
      dependsOn        = [{ containerName = "certificats", condition = "SUCCESS" }]
      logConfiguration = local.journaux["${each.key}-sentinelle"]
    },
  ])
}

locals {
  environnement_sentinelle = {
    for nom, n in merge(local.noeuds, { "kafka-admin" = { creneau = 0 } }) : nom => [
      { name = "Ledger__Sentinelle__Secret", value = nom == "kafka-admin" ? var.secret_administration : var.secrets_noeuds[nom] },
      { name = "Ledger__Sentinelle__Dossier", value = "/certificats" },
      { name = "Ledger__Sentinelle__Serveurs", value = local.serveurs },
      { name = "Ledger__Sentinelle__NombreNoeuds", value = "3" },
      { name = "Ledger__Sentinelle__Noeud", value = nom },
      { name = "Ledger__Sentinelle__Creneau", value = tostring(n.creneau) },
      { name = "Ledger__Sentinelle__DossierDonnees", value = "/donnees" },
      { name = "Ledger__Sentinelle__Groupes__0", value = "ledger-projections" },
      { name = "DOTNET_EnableDiagnostics", value = "0" },
    ]
  }

  journaux = merge(
    {
      for paire in setproduct(keys(local.noeuds), ["certificats", "kafka", "sentinelle"]) : "${paire[0]}-${paire[1]}" => {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.kafka.name
          "awslogs-region"        = var.region
          "awslogs-stream-prefix" = "${paire[0]}-${paire[1]}"
        }
      }
    },
    {
      for c in ["certificats", "initialisation"] : "initialisation-${c}" => {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.kafka.name
          "awslogs-region"        = var.region
          "awslogs-stream-prefix" = "initialisation-${c}"
        }
      }
    },
  )
}

resource "aws_ecs_service" "noeud" {
  for_each = local.services_actifs ? local.noeuds : {}

  name            = each.key
  cluster         = var.arn_grappe
  task_definition = aws_ecs_task_definition.noeud[each.key].arn
  desired_count   = 1
  launch_type     = "EC2"

  # Une seule tâche par nœud (même volume, même nom) : arrêt puis démarrage.
  deployment_minimum_healthy_percent = 0
  deployment_maximum_percent         = 100

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  network_configuration {
    subnets         = [each.value.sous_reseau]
    security_groups = [aws_security_group.noeuds.id]
  }

  service_registries {
    registry_arn = aws_service_discovery_service.noeud[each.key].arn
  }

  depends_on = [aws_volume_attachment.donnees]
}

# --- Tâche d'initialisation (sujets et ACL), lancée à la demande ----------------------------

resource "aws_security_group" "initialisation" {
  #checkov:skip=CKV2_AWS_5:Attaché à chaque lancement de la tâche (run-task), pas à une ressource Terraform.
  name        = "${local.nom}-initialisation"
  description = "Tache d initialisation Kafka : sortie mTLS vers les noeuds et HTTPS vers les points de terminaison"
  vpc_id      = var.vpc_id

  tags = {
    Name = "${local.nom}-initialisation"
  }
}

resource "aws_vpc_security_group_egress_rule" "initialisation_vers_noeuds" {
  security_group_id            = aws_security_group.initialisation.id
  description                  = "mTLS vers les noeuds Kafka"
  ip_protocol                  = "tcp"
  from_port                    = local.port_client
  to_port                      = local.port_client
  referenced_security_group_id = aws_security_group.noeuds.id
}

resource "aws_vpc_security_group_egress_rule" "initialisation_vers_points_de_terminaison" {
  security_group_id            = aws_security_group.initialisation.id
  description                  = "HTTPS vers les points de terminaison VPC"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = var.groupe_securite_points_de_terminaison
}

resource "aws_vpc_security_group_egress_rule" "initialisation_vers_s3" {
  security_group_id = aws_security_group.initialisation.id
  description       = "HTTPS vers S3 (couches des images ECR)"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  prefix_list_id    = data.aws_ec2_managed_prefix_list.s3.id
}

resource "aws_vpc_security_group_ingress_rule" "noeuds_depuis_initialisation" {
  security_group_id            = aws_security_group.noeuds.id
  description                  = "mTLS depuis la tache d initialisation"
  ip_protocol                  = "tcp"
  from_port                    = local.port_client
  to_port                      = local.port_client
  referenced_security_group_id = aws_security_group.initialisation.id
}

resource "aws_ecs_task_definition" "initialisation" {
  count = local.services_actifs ? 1 : 0

  family                   = "${local.nom}-initialisation"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.sentinelle["kafka-admin"].arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  volume {
    name = "certificats"
  }

  volume {
    name = "tmp"
  }

  container_definitions = jsonencode([
    {
      name                   = "certificats"
      image                  = local.image_sentinelle
      essential              = false
      user                   = "1000"
      readonlyRootFilesystem = true
      command                = ["installer"]
      environment            = local.environnement_sentinelle["kafka-admin"]
      mountPoints            = [{ sourceVolume = "certificats", containerPath = "/certificats", readOnly = false }]
      logConfiguration       = local.journaux["initialisation-certificats"]
    },
    {
      name                   = "initialisation"
      image                  = local.image_kafka
      essential              = true
      user                   = "1000"
      readonlyRootFilesystem = true
      entryPoint             = ["sh", "-c"]
      command                = [local.script_initialisation]
      mountPoints = [
        { sourceVolume = "certificats", containerPath = "/certificats", readOnly = true },
        { sourceVolume = "tmp", containerPath = "/tmp", readOnly = false },
      ]
      dependsOn        = [{ containerName = "certificats", condition = "SUCCESS" }]
      logConfiguration = local.journaux["initialisation-initialisation"]
    },
  ])
}

# --- Alarmes (sans action : sujet SNS en phase 11) -----------------------------------------

resource "aws_cloudwatch_metric_alarm" "noeuds_absents" {
  for_each = local.services_actifs ? local.noeuds : {}

  alarm_name          = "${var.prefixe}-${each.key}-noeuds-absents"
  alarm_description   = "Le noeud ${each.key} voit moins de 3 noeuds (ou sa sentinelle ne publie plus)."
  namespace           = "Ledger/Kafka"
  metric_name         = "NoeudsPresents"
  dimensions          = { Noeud = each.key }
  statistic           = "Minimum"
  period              = 300
  evaluation_periods  = 2
  comparison_operator = "LessThanThreshold"
  threshold           = 3
  treat_missing_data  = "breaching"
}

resource "aws_cloudwatch_metric_alarm" "sous_replication" {
  for_each = local.services_actifs ? local.noeuds : {}

  alarm_name          = "${var.prefixe}-${each.key}-partitions-sous-repliquees"
  alarm_description   = "Partitions sous-repliquees vues par ${each.key} pendant 10 minutes."
  namespace           = "Ledger/Kafka"
  metric_name         = "PartitionsSousRepliquees"
  dimensions          = { Noeud = each.key }
  statistic           = "Maximum"
  period              = 300
  evaluation_periods  = 2
  comparison_operator = "GreaterThanThreshold"
  threshold           = 0
  treat_missing_data  = "breaching"
}

resource "aws_cloudwatch_metric_alarm" "espace_disque" {
  for_each = local.services_actifs ? local.noeuds : {}

  alarm_name          = "${var.prefixe}-${each.key}-espace-disque"
  alarm_description   = "Moins de 20 % d espace libre sur le volume de donnees de ${each.key}."
  namespace           = "Ledger/Kafka"
  metric_name         = "EspaceDisqueLibrePourcent"
  dimensions          = { Noeud = each.key }
  statistic           = "Minimum"
  period              = 300
  evaluation_periods  = 1
  comparison_operator = "LessThanThreshold"
  threshold           = 20
  treat_missing_data  = "breaching"
}

resource "aws_cloudwatch_metric_alarm" "retard_projections" {
  count = local.services_actifs ? 1 : 0

  alarm_name          = "${var.prefixe}-kafka-retard-projections"
  alarm_description   = "Le groupe ledger-projections a plus de ${var.seuil_retard_projections} messages de retard pendant 15 minutes."
  namespace           = "Ledger/Kafka"
  metric_name         = "RetardGroupe"
  dimensions          = { Groupe = "ledger-projections" }
  statistic           = "Maximum"
  period              = 300
  evaluation_periods  = 3
  comparison_operator = "GreaterThanThreshold"
  threshold           = var.seuil_retard_projections
  treat_missing_data  = "notBreaching"
}
