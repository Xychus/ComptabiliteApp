# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {
  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/ledger/ledger-dev-query-api"
    }
  }

  mock_resource "aws_cloudwatch_log_group" {
    defaults = {
      arn = "arn:aws:logs:ca-central-1:111122223333:log-group:/ledger-dev/query-api"
    }
  }

  mock_resource "aws_ecs_task_definition" {
    defaults = {
      arn = "arn:aws:ecs:ca-central-1:111122223333:task-definition/ledger-dev-query-api:1"
    }
  }
}

variables {
  prefixe                               = "ledger-dev"
  nom                                   = "query-api"
  region                                = "ca-central-1"
  compte_aws                            = "111122223333"
  vpc_id                                = "vpc-0123456789abcdef0"
  sous_reseaux                          = ["subnet-0000000000000001a", "subnet-0000000000000001b"]
  arn_grappe                            = "arn:aws:ecs:ca-central-1:111122223333:cluster/ledger-dev"
  nom_grappe                            = "ledger-dev"
  groupe_securite_points_de_terminaison = "sg-0000000000000bbbb"
  depot_ecr_url                         = "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/query-api"
  depot_ecr_arn                         = "arn:aws:ecr:ca-central-1:111122223333:repository/ledger/query-api"
  version_image                         = "0.4.2"
  depot_collecteur_url                  = "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/aws-otel-collector"
  depot_collecteur_arn                  = "arn:aws:ecr:ca-central-1:111122223333:repository/ledger/aws-otel-collector"
  groupe_journaux_metriques             = "/ledger-dev/metriques-api"
  cle_kms_secrets_arn                   = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-4444-4a5b-9c3d-0123456789ab"
  cle_kms_journaux_arn                  = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-1111-4a5b-9c3d-0123456789ab"
  cle_kms_xray_arn                      = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-8888-4a5b-9c3d-0123456789ab"
  frontiere_permissions_arn             = "arn:aws:iam::111122223333:policy/bootstrap/ledger-frontiere-charges"
  port_https                            = 8443
  groupe_cible_arn                      = "arn:aws:elasticloadbalancing:ca-central-1:111122223333:targetgroup/ledger-dev-query/abc"
  groupe_securite_equilibreur           = "sg-0000000000000dddd"

  environnement = {
    ConnectionStrings__Lecture = "Host=ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com;Database=ledger;Username=ledger_query;SSL Mode=VerifyFull"
  }

  secrets = {
    Ledger__Tls__Chaine = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/pki/query-api-a:chaine::"
    Ledger__Tls__Cle    = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/pki/query-api-a:cle::"
  }
  arn_secrets_lus = ["arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/pki/query-api-a"]

  sorties = {
    aurora = { groupe_securite = "sg-0000000000000aaaa", port = 5432 }
  }

  nombre_taches  = 2
  mise_a_echelle = { maximum = 4 }
}

run "image_exacte_lecture_seule_non_root_secrets_ecs" {
  command = apply

  assert {
    condition     = jsondecode(aws_ecs_task_definition.service[0].container_definitions)[0].image == "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/query-api:0.4.2"
    error_message = "Image par version SemVer exacte."
  }

  assert {
    condition     = jsondecode(aws_ecs_task_definition.service[0].container_definitions)[0].readonlyRootFilesystem && jsondecode(aws_ecs_task_definition.service[0].container_definitions)[0].user == "1654"
    error_message = "Système de fichiers en lecture seule, utilisateur non root."
  }

  assert {
    condition     = toset([for s in jsondecode(aws_ecs_task_definition.service[0].container_definitions)[0].secrets : s.name]) == toset(["Ledger__Tls__Chaine", "Ledger__Tls__Cle"])
    error_message = "Le certificat de la PKI est injecté par secrets ECS."
  }

  assert {
    condition     = { for e in jsondecode(aws_ecs_task_definition.service[0].container_definitions)[0].environment : e.name => e.value }["OTEL_EXPORTER_OTLP_ENDPOINT"] == "http://127.0.0.1:4317"
    error_message = "Les traces partent vers le collecteur ADOT local."
  }

  assert {
    condition     = strcontains(one([for e in jsondecode(aws_ecs_task_definition.service[0].container_definitions)[1].environment : e.value if e.name == "AOT_CONFIG_CONTENT"]), "/ledger-dev/metriques-api")
    error_message = "Les métriques EMF vont dans le groupe de journaux chiffré par CMK."
  }
}

run "deploiement_sur_et_mise_a_l_echelle" {
  command = apply

  assert {
    condition     = aws_ecs_service.service[0].deployment_circuit_breaker[0].rollback && aws_ecs_service.service[0].deployment_minimum_healthy_percent == 100
    error_message = "Disjoncteur de déploiement avec retour arrière, sans interruption."
  }

  assert {
    condition     = aws_appautoscaling_target.service[0].min_capacity == 2 && aws_appautoscaling_target.service[0].max_capacity == 4
    error_message = "Mise à l'échelle entre le nombre de tâches et le maximum."
  }

  assert {
    condition     = aws_vpc_security_group_ingress_rule.entree_destination["aurora"].security_group_id == "sg-0000000000000aaaa" && aws_vpc_security_group_ingress_rule.depuis_equilibreur[0].from_port == 8443
    error_message = "Aurora accepte le service ; le service n'accepte que l'équilibreur, sur HTTPS."
  }
}

run "secret_en_clair_refuse" {
  command = plan

  variables {
    environnement = { Ledger__Tls__Cle = "-----BEGIN PRIVATE KEY-----" }
  }

  expect_failures = [var.environnement]
}

run "mot_de_passe_dans_une_chaine_refuse" {
  command = plan

  variables {
    environnement = { ConnectionStrings__Ledger = "Host=h;Password=secret" }
  }

  expect_failures = [var.environnement]
}

run "latest_refuse" {
  command = plan

  variables {
    version_image = "latest"
  }

  expect_failures = [var.version_image]
}

run "sans_image_aucun_service" {
  command = apply

  variables {
    version_image = null
  }

  assert {
    condition     = length(aws_ecs_service.service) == 0 && length(aws_ecs_task_definition.service) == 0
    error_message = "Sans image publiée, aucun service ni définition de tâche."
  }
}
