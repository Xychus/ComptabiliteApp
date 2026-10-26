# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {
  mock_data "aws_ssm_parameter" {
    defaults = {
      value = "ami-0123456789abcdef0"
    }
  }

  mock_data "aws_subnet" {
    defaults = {
      availability_zone = "ca-central-1a"
    }
  }

  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/ledger/ledger-dev-kafka"
    }
  }

  mock_resource "aws_cloudwatch_log_group" {
    defaults = {
      arn = "arn:aws:logs:ca-central-1:111122223333:log-group:/ledger-dev/kafka"
    }
  }

  mock_resource "aws_ebs_volume" {
    defaults = {
      id = "vol-0123456789abcdef0"
    }
  }

  mock_resource "aws_service_discovery_service" {
    defaults = {
      arn = "arn:aws:servicediscovery:ca-central-1:111122223333:service/srv-abc"
    }
  }

  mock_resource "aws_ecs_task_definition" {
    defaults = {
      arn = "arn:aws:ecs:ca-central-1:111122223333:task-definition/ledger-dev-kafka-1:1"
    }
  }
}

variables {
  prefixe                               = "ledger-dev"
  region                                = "ca-central-1"
  compte_aws                            = "111122223333"
  vpc_id                                = "vpc-0123456789abcdef0"
  sous_reseaux                          = ["subnet-0000000000000000a", "subnet-0000000000000000b", "subnet-0000000000000000c"]
  sous_reseaux_applications             = ["subnet-0000000000000001a", "subnet-0000000000000001b", "subnet-0000000000000001c"]
  domaine_interne                       = "ledger-dev.internal"
  nom_grappe                            = "ledger-dev"
  arn_grappe                            = "arn:aws:ecs:ca-central-1:111122223333:cluster/ledger-dev"
  groupe_securite_points_de_terminaison = "sg-0000000000000bbbb"
  identifiant_grappe_kraft              = "0TqLSlDtQHOLxtu682Oxmg"
  depot_kafka_url                       = "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/kafka"
  depot_kafka_arn                       = "arn:aws:ecr:ca-central-1:111122223333:repository/ledger/kafka"
  depot_sentinelle_url                  = "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/pki-sentinelle"
  depot_sentinelle_arn                  = "arn:aws:ecr:ca-central-1:111122223333:repository/ledger/pki-sentinelle"
  version_sentinelle                    = "0.1.0"
  secret_administration                 = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/pki/kafka-admin-a"
  cle_kms_kafka_arn                     = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-2222-4a5b-9c3d-0123456789ab"
  cle_kms_secrets_arn                   = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-4444-4a5b-9c3d-0123456789ab"
  cle_kms_journaux_arn                  = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-1111-4a5b-9c3d-0123456789ab"
  frontiere_permissions_arn             = "arn:aws:iam::111122223333:policy/bootstrap/ledger-frontiere-charges"

  secrets_noeuds = {
    kafka-1 = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/pki/kafka-1-a"
    kafka-2 = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/pki/kafka-2-a"
    kafka-3 = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/pki/kafka-3-a"
  }

  acl = {
    debezium           = { sujets_ecriture = ["ledger.events"], prefixes_sujets = ["ledger-connect-"], groupes = ["ledger-connect"] }
    projections-worker = { sujets_lecture = ["ledger.events"], groupes = ["ledger-projections"] }
  }
}

run "trois_noeuds_un_par_zone_avec_volume_persistant_chiffre" {
  command = apply

  assert {
    condition     = length(aws_instance.noeud) == 3 && length(aws_ecs_service.noeud) == 3
    error_message = "Trois nœuds, chacun avec son instance et son service."
  }

  assert {
    condition     = alltrue([for v in aws_ebs_volume.donnees : v.encrypted && v.kms_key_id == var.cle_kms_kafka_arn])
    error_message = "Les volumes de données sont chiffrés par la CMK « kafka »."
  }

  assert {
    condition     = alltrue([for i in aws_instance.noeud : i.root_block_device[0].encrypted && i.root_block_device[0].kms_key_id == var.cle_kms_kafka_arn])
    error_message = "Les volumes racines sont chiffrés par la CMK « kafka »."
  }

  assert {
    condition     = alltrue([for i in aws_instance.noeud : i.metadata_options[0].http_tokens == "required" && i.metadata_options[0].http_put_response_hop_limit == 1])
    error_message = "IMDSv2 exigé, inaccessible depuis un conteneur."
  }

  assert {
    condition     = aws_instance.noeud["kafka-2"].subnet_id == "subnet-0000000000000000b" && strcontains(aws_instance.noeud["kafka-2"].user_data, "ECS_AWSVPC_BLOCK_IMDS=true")
    error_message = "kafka-N est dans la N-ième zone ; l'IMDS est bloqué pour les tâches."
  }

  assert {
    condition     = alltrue([for s in aws_ecs_service.noeud : s.deployment_maximum_percent == 100 && s.deployment_minimum_healthy_percent == 0 && s.deployment_circuit_breaker[0].rollback])
    error_message = "Une tâche par nœud (arrêt puis démarrage), disjoncteur avec retour arrière."
  }
}

run "ecouteurs_ssl_seulement_mtls_tls13_refus_par_defaut" {
  command = apply

  assert {
    condition = alltrue([
      for e in jsondecode(aws_ecs_task_definition.noeud["kafka-1"].container_definitions)[1].environment :
      !strcontains(e.value, "PLAINTEXT")
    ])
    error_message = "Aucun écouteur en clair."
  }

  assert {
    condition     = { for e in jsondecode(aws_ecs_task_definition.noeud["kafka-1"].container_definitions)[1].environment : e.name => e.value }["KAFKA_LISTENER_SECURITY_PROTOCOL_MAP"] == "MTLS:SSL,CONTROLLER:SSL"
    error_message = "Les deux écouteurs sont en SSL."
  }

  assert {
    condition = (
      { for e in jsondecode(aws_ecs_task_definition.noeud["kafka-1"].container_definitions)[1].environment : e.name => e.value }["KAFKA_SSL_CLIENT_AUTH"] == "required"
      && { for e in jsondecode(aws_ecs_task_definition.noeud["kafka-1"].container_definitions)[1].environment : e.name => e.value }["KAFKA_SSL_ENABLED_PROTOCOLS"] == "TLSv1.3"
      && { for e in jsondecode(aws_ecs_task_definition.noeud["kafka-1"].container_definitions)[1].environment : e.name => e.value }["KAFKA_ALLOW_EVERYONE_IF_NO_ACL_FOUND"] == "false"
    )
    error_message = "mTLS exigé, TLS 1.3 seul, refus par défaut."
  }

  assert {
    condition     = { for e in jsondecode(aws_ecs_task_definition.noeud["kafka-3"].container_definitions)[1].environment : e.name => e.value }["KAFKA_ADVERTISED_LISTENERS"] == "MTLS://kafka-3.kafka.ledger-dev.internal:9093"
    error_message = "Chaque nœud annonce son nom stable (SAN de son certificat)."
  }

  assert {
    condition     = jsondecode(aws_ecs_task_definition.noeud["kafka-1"].container_definitions)[1].image == "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/kafka:4.3.2"
    error_message = "Image apache/kafka copiée dans ECR, étiquette exacte."
  }

  assert {
    condition     = output.serveurs == "kafka-1.kafka.ledger-dev.internal:9093,kafka-2.kafka.ledger-dev.internal:9093,kafka-3.kafka.ledger-dev.internal:9093"
    error_message = "Les serveurs d'amorçage sont les trois noms stables."
  }
}

run "sentinelle_par_noeud_au_moindre_privilege" {
  command = apply

  assert {
    condition     = one([for s in jsondecode(aws_iam_role_policy.sentinelle["kafka-2"].policy).Statement : s.Resource if s.Sid == "CertificatDuNoeud"]) == var.secrets_noeuds["kafka-2"]
    error_message = "La sentinelle d'un nœud ne lit que le secret de ce nœud."
  }

  assert {
    condition     = { for e in jsondecode(aws_ecs_task_definition.noeud["kafka-2"].container_definitions)[2].environment : e.name => e.value }["Ledger__Sentinelle__Creneau"] == "1"
    error_message = "Chaque nœud a son créneau de redémarrage."
  }

  assert {
    condition     = jsondecode(aws_ecs_task_definition.noeud["kafka-1"].container_definitions)[2].readonlyRootFilesystem && jsondecode(aws_ecs_task_definition.noeud["kafka-1"].container_definitions)[2].essential
    error_message = "La sentinelle est essentielle (son arrêt relance la tâche) et en lecture seule."
  }
}

run "initialisation_cree_le_sujet_et_les_acl" {
  command = apply

  assert {
    condition     = strcontains(jsondecode(aws_ecs_task_definition.initialisation[0].container_definitions)[1].command[0], "--topic ledger.events --partitions 6 --replication-factor 3 --config min.insync.replicas=2")
    error_message = "Le sujet ledger.events est créé avec 6 partitions, réplication 3, ISR minimal 2."
  }

  assert {
    condition     = strcontains(jsondecode(aws_ecs_task_definition.initialisation[0].container_definitions)[1].command[0], "--allow-principal User:projections-worker --operation Read --operation Describe --topic ledger.events")
    error_message = "Le worker lit ledger.events."
  }

  assert {
    condition     = strcontains(jsondecode(aws_ecs_task_definition.initialisation[0].container_definitions)[1].command[0], "--allow-principal User:debezium --resource-pattern-type prefixed")
    error_message = "Debezium gère ses sujets internes (préfixe)."
  }

  assert {
    condition     = !strcontains(jsondecode(aws_ecs_task_definition.initialisation[0].container_definitions)[1].command[0], "User:projections-worker --operation Write")
    error_message = "Le worker n'écrit dans aucun sujet."
  }
}

run "sans_image_de_sentinelle_aucun_service" {
  command = apply

  variables {
    version_sentinelle = null
  }

  assert {
    condition     = length(aws_ecs_service.noeud) == 0 && length(aws_instance.noeud) == 3
    error_message = "Sans image publiée, les instances et volumes existent mais aucun service."
  }
}

run "secrets_incomplets_refuses" {
  command = plan

  variables {
    secrets_noeuds = { kafka-1 = "arn:a" }
  }

  expect_failures = [var.secrets_noeuds]
}
