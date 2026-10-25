# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {
  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/ledger/ledger-dev-debezium"
    }
  }

  mock_resource "aws_cloudwatch_log_group" {
    defaults = {
      arn = "arn:aws:logs:ca-central-1:111122223333:log-group:/ledger-dev/debezium"
    }
  }

  mock_resource "aws_ecs_task_definition" {
    defaults = {
      arn = "arn:aws:ecs:ca-central-1:111122223333:task-definition/ledger-dev-debezium:1"
    }
  }
}

variables {
  prefixe                               = "ledger-dev"
  region                                = "ca-central-1"
  compte_aws                            = "111122223333"
  vpc_id                                = "vpc-0123456789abcdef0"
  sous_reseaux                          = ["subnet-0000000000000001a", "subnet-0000000000000001b"]
  arn_grappe                            = "arn:aws:ecs:ca-central-1:111122223333:cluster/ledger-dev"
  groupe_securite_points_de_terminaison = "sg-0000000000000bbbb"
  depot_debezium_url                    = "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/debezium-connect"
  depot_debezium_arn                    = "arn:aws:ecr:ca-central-1:111122223333:repository/ledger/debezium-connect"
  depot_sentinelle_url                  = "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/pki-sentinelle"
  depot_sentinelle_arn                  = "arn:aws:ecr:ca-central-1:111122223333:repository/ledger/pki-sentinelle"
  version_sentinelle                    = "0.1.0"
  serveurs_kafka                        = "kafka-1.kafka.ledger-dev.internal:9093,kafka-2.kafka.ledger-dev.internal:9093"
  groupe_securite_kafka                 = "sg-0000000000000cccc"
  secret_certificat                     = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/pki/debezium-a"
  secret_mot_de_passe                   = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/postgres/debezium-a"
  hote_aurora                           = "ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com"
  nom_base                              = "ledger"
  groupe_securite_aurora                = "sg-0000000000000aaaa"
  certificat_racine_rds_pem             = "-----BEGIN CERTIFICATE-----\nrds\n-----END CERTIFICATE-----\n"
  cle_kms_secrets_arn                   = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-4444-4a5b-9c3d-0123456789ab"
  cle_kms_journaux_arn                  = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-1111-4a5b-9c3d-0123456789ab"
  frontiere_permissions_arn             = "arn:aws:iam::111122223333:policy/bootstrap/ledger-frontiere-charges"
}

run "connecteur_invariant_et_parametres_aurora_verify_full" {
  command = apply

  assert {
    condition     = output.configuration_connecteur["transforms.outbox.route.topic.replacement"] == "$${routedByValue}"
    error_message = "La configuration invariante est reprise telle quelle (aucune interpolation)."
  }

  assert {
    condition = (output.configuration_connecteur["database.sslmode"] == "verify-full"
      && output.configuration_connecteur["database.sslrootcert"] == "/certificats/rds-ca.pem"
    && output.configuration_connecteur["database.hostname"] == var.hote_aurora)
    error_message = "Connexion verify-full au rédacteur Aurora contre les certificats racines RDS."
  }

  assert {
    condition     = output.configuration_connecteur["database.password"] == "$${env:LEDGER_DEBEZIUM_PASSWORD}"
    error_message = "Le mot de passe est une référence au fournisseur de configuration, jamais une valeur."
  }
}

run "aucun_secret_en_clair_et_mtls_vers_kafka" {
  command = apply

  assert {
    condition     = one(jsondecode(aws_ecs_task_definition.debezium[0].container_definitions)[1].secrets).valueFrom == "${var.secret_mot_de_passe}:mot_de_passe::"
    error_message = "Le mot de passe est injecté par secrets ECS depuis Secrets Manager."
  }

  assert {
    condition = alltrue([
      for e in jsondecode(aws_ecs_task_definition.debezium[0].container_definitions)[1].environment :
      !strcontains(lower(e.name), "password")
    ])
    error_message = "Aucun mot de passe en variable d'environnement en clair."
  }

  assert {
    condition = (
      { for e in jsondecode(aws_ecs_task_definition.debezium[0].container_definitions)[1].environment : e.name => e.value }["CONNECT_PRODUCER_SECURITY_PROTOCOL"] == "SSL"
      && { for e in jsondecode(aws_ecs_task_definition.debezium[0].container_definitions)[1].environment : e.name => e.value }["CONNECT_SSL_ENABLED_PROTOCOLS"] == "TLSv1.3"
    )
    error_message = "Worker et producteurs en mTLS TLS 1.3 vers Kafka."
  }

  assert {
    condition     = jsondecode(aws_ecs_task_definition.debezium[0].container_definitions)[1].image == "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/debezium-connect:3.7.0.Final"
    error_message = "Image Debezium copiée dans ECR, étiquette exacte."
  }

  assert {
    condition     = aws_vpc_security_group_ingress_rule.kafka_depuis_debezium.security_group_id == var.groupe_securite_kafka && aws_vpc_security_group_ingress_rule.aurora_depuis_debezium.from_port == 5432
    error_message = "Kafka et Aurora acceptent Debezium (règles posées sur leurs groupes)."
  }
}

run "redeploiement_sans_interruption_et_alarmes" {
  command = apply

  assert {
    condition     = aws_ecs_service.debezium[0].deployment_minimum_healthy_percent == 100 && aws_ecs_service.debezium[0].deployment_circuit_breaker[0].rollback
    error_message = "Le nouveau worker démarre avant l'arrêt de l'ancien ; retour arrière automatique."
  }

  assert {
    condition     = strcontains(aws_cloudwatch_log_metric_filter.echec_connecteur.pattern, "unrecoverable") && aws_cloudwatch_metric_alarm.echec_connecteur.threshold == 0
    error_message = "Un échec du connecteur ou de sa tâche déclenche l'alarme."
  }
}

run "version_debezium_inexacte_refusee" {
  command = plan

  variables {
    version_debezium = "3.7"
  }

  expect_failures = [var.version_debezium]
}
