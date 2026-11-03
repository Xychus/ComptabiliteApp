# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {
  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/ledger/ledger-dev-keycloak"
    }
  }

  mock_resource "aws_cloudwatch_log_group" {
    defaults = {
      arn = "arn:aws:logs:ca-central-1:111122223333:log-group:/ledger-dev/keycloak"
    }
  }

  mock_resource "aws_ecs_task_definition" {
    defaults = {
      arn = "arn:aws:ecs:ca-central-1:111122223333:task-definition/ledger-dev-keycloak:1"
    }
  }

  mock_resource "aws_service_discovery_service" {
    defaults = {
      arn = "arn:aws:servicediscovery:ca-central-1:111122223333:service/srv-0123456789abcdef"
    }
  }
}

variables {
  prefixe                               = "ledger-dev"
  region                                = "ca-central-1"
  compte_aws                            = "111122223333"
  vpc_id                                = "vpc-0123456789abcdef0"
  cidr_vpc                              = "10.20.0.0/16"
  sous_reseaux                          = ["subnet-0000000000000001a", "subnet-0000000000000001b"]
  arn_grappe                            = "arn:aws:ecs:ca-central-1:111122223333:cluster/ledger-dev"
  domaine_interne                       = "ledger-dev.internal"
  groupe_securite_points_de_terminaison = "sg-0000000000000bbbb"
  depot_keycloak_url                    = "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/keycloak"
  depot_keycloak_arn                    = "arn:aws:ecr:ca-central-1:111122223333:repository/ledger/keycloak"
  version_keycloak                      = "26.8.1"
  depot_sentinelle_url                  = "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/pki-sentinelle"
  depot_sentinelle_arn                  = "arn:aws:ecr:ca-central-1:111122223333:repository/ledger/pki-sentinelle"
  version_sentinelle                    = "0.1.0"
  serveurs_kafka                        = "kafka-1.kafka.ledger-dev.internal:9093"
  secret_certificat                     = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/pki/keycloak-a"
  secret_mot_de_passe                   = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/postgres/keycloak-a"
  hote_aurora                           = "ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com"
  nom_base                              = "ledger"
  groupe_securite_aurora                = "sg-0000000000000aaaa"
  certificat_racine_rds_pem             = "-----BEGIN CERTIFICATE-----\nrds\n-----END CERTIFICATE-----\n"
  cle_kms_secrets_arn                   = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-4444-4a5b-9c3d-0123456789ab"
  cle_kms_journaux_arn                  = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-1111-4a5b-9c3d-0123456789ab"
  frontiere_permissions_arn             = "arn:aws:iam::111122223333:policy/bootstrap/ledger-frontiere-charges"
}

run "https_pki_tls13_mtls_et_emetteur_stable" {
  command = apply

  assert {
    condition     = output.url_realm == "https://keycloak.idp.ledger-dev.internal:8443/realms/ledger" && output.nom_hote == "keycloak.idp.ledger-dev.internal"
    error_message = "Émetteur des jetons : nom Cloud Map stable, port 8443, realm ledger."
  }

  assert {
    condition = (output.environnement["KC_HTTPS_PROTOCOLS"] == "TLSv1.3"
      && output.environnement["KC_HTTPS_CLIENT_AUTH"] == "request"
      && output.environnement["KC_HTTP_ENABLED"] == "false"
      && output.environnement["KC_HTTPS_CERTIFICATE_FILE"] == "/certificats/keystore.pem"
      && output.environnement["KC_HTTPS_CERTIFICATE_KEY_FILE"] == "/certificats/keystore.pem"
    && output.environnement["KC_HTTPS_TRUST_STORE_FILE"] == "/certificats/truststore.pem")
    error_message = "HTTPS seulement, TLS 1.3, mTLS demandé, fichiers écrits par la sentinelle (racine de la PKI en magasin de confiance)."
  }

  assert {
    condition     = output.environnement["KC_HOSTNAME"] == "https://keycloak.idp.ledger-dev.internal:8443"
    error_message = "Nom d'hôte fixé (hostname-strict) : l'émetteur ne dépend pas de la requête."
  }

  assert {
    condition     = jsondecode(output.environnement["LEDGER_REALM"]) == jsondecode(file("../../keycloak/ledger-realm.json"))
    error_message = "Le realm importé est infra/keycloak/ledger-realm.json, tel quel."
  }
}

run "base_verify_full_sans_secret_en_clair_ni_compte_d_administration" {
  command = apply

  assert {
    condition = (output.environnement["KC_DB_URL"] == "jdbc:postgresql://${var.hote_aurora}:5432/ledger?sslmode=verify-full&sslrootcert=/certificats/rds-ca.pem"
      && output.environnement["KC_DB_SCHEMA"] == "keycloak"
    && output.environnement["KC_DB_USERNAME"] == "keycloak")
    error_message = "Schéma keycloak d'Aurora, rôle keycloak, TLS verify-full contre les racines RDS."
  }

  assert {
    condition     = one(jsondecode(aws_ecs_task_definition.keycloak[0].container_definitions)[1].secrets) == { name = "KC_DB_PASSWORD", valueFrom = "${var.secret_mot_de_passe}:mot_de_passe::" }
    error_message = "Le mot de passe est injecté par secrets ECS depuis Secrets Manager."
  }

  assert {
    condition = alltrue([
      for e in jsondecode(aws_ecs_task_definition.keycloak[0].container_definitions)[1].environment :
      !strcontains(lower(e.name), "password") && !strcontains(e.name, "BOOTSTRAP_ADMIN")
    ])
    error_message = "Ni mot de passe en clair ni compte d'administration (ADR 0030)."
  }

  assert {
    condition = (jsondecode(aws_ecs_task_definition.keycloak[0].container_definitions)[1].user == "1001:0"
    && jsondecode(aws_ecs_task_definition.keycloak[0].container_definitions)[1].image == "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/keycloak:26.8.1")
    error_message = "Image copiée dans ECR à l'étiquette exacte ; utilisateur de la sentinelle (fichiers 0600)."
  }
}

run "reseau_decouverte_et_redeploiement" {
  command = apply

  assert {
    condition     = aws_vpc_security_group_ingress_rule.https_depuis_vpc.from_port == 8443 && aws_vpc_security_group_ingress_rule.https_depuis_vpc.cidr_ipv4 == var.cidr_vpc
    error_message = "Seul HTTPS 8443 est ouvert au VPC (l'interface de gestion 9000 ne l'est pas)."
  }

  assert {
    condition     = aws_vpc_security_group_ingress_rule.aurora_depuis_keycloak.security_group_id == var.groupe_securite_aurora
    error_message = "Aurora accepte Keycloak (règle posée sur son groupe)."
  }

  assert {
    condition     = aws_service_discovery_private_dns_namespace.idp.name == "idp.ledger-dev.internal"
    error_message = "Espace de noms Cloud Map dédié."
  }

  assert {
    condition     = aws_ecs_service.keycloak[0].deployment_minimum_healthy_percent == 100 && aws_ecs_service.keycloak[0].deployment_circuit_breaker[0].rollback
    error_message = "La nouvelle tâche démarre avant l'arrêt de l'ancienne ; retour arrière automatique."
  }
}

run "sans_sentinelle_publiee_aucun_service" {
  command = plan

  variables {
    version_sentinelle = null
  }

  assert {
    condition     = length(aws_ecs_service.keycloak) == 0 && length(aws_ecs_task_definition.keycloak) == 0
    error_message = "Aucune tâche ni service tant que la sentinelle n'est pas publiée."
  }
}

run "version_keycloak_inexacte_refusee" {
  command = plan

  variables {
    version_keycloak = "26.8"
  }

  expect_failures = [var.version_keycloak]
}
