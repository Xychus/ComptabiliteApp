# Fournisseurs simulés : aucun appel AWS, aucune clé réelle.
mock_provider "aws" {
  mock_resource "aws_lb" {
    defaults = {
      arn        = "arn:aws:elasticloadbalancing:ca-central-1:111122223333:loadbalancer/app/ledger-dev-api/abc"
      arn_suffix = "app/ledger-dev-api/abc"
      dns_name   = "internal-ledger-dev-api-123.ca-central-1.elb.amazonaws.com"
      zone_id    = "ZQSVJUPU6J1EY"
    }
  }

  mock_resource "aws_lb_target_group" {
    defaults = {
      arn        = "arn:aws:elasticloadbalancing:ca-central-1:111122223333:targetgroup/ledger-dev-cible/abc"
      arn_suffix = "targetgroup/ledger-dev-cible/abc"
    }
  }

  mock_resource "aws_lb_listener" {
    defaults = {
      arn = "arn:aws:elasticloadbalancing:ca-central-1:111122223333:listener/app/ledger-dev-api/abc/def"
    }
  }

  mock_resource "aws_acm_certificate" {
    defaults = {
      arn = "arn:aws:acm:ca-central-1:111122223333:certificate/abc"
    }
  }
}

mock_provider "tls" {}

variables {
  prefixe      = "ledger-dev"
  vpc_id       = "vpc-0123456789abcdef0"
  cidr_vpc     = "10.20.0.0/16"
  sous_reseaux = ["subnet-0000000000000001a", "subnet-0000000000000001b", "subnet-0000000000000001c"]
  zone_interne = { id = "Z0123456789ABC", domaine = "ledger-dev.internal" }
}

run "https_seulement_tls13_privilegie_interne" {
  command = apply

  assert {
    condition     = aws_lb.api.internal && aws_lb.api.drop_invalid_header_fields
    error_message = "Équilibreur interne, en-têtes invalides rejetés."
  }

  assert {
    condition     = aws_lb_listener.https.protocol == "HTTPS" && aws_lb_listener.https.ssl_policy == "ELBSecurityPolicy-TLS13-1-2-Res-2021-06"
    error_message = "Écoute HTTPS seulement, politique TLS 1.3 privilégiée."
  }

  assert {
    condition     = alltrue([for g in aws_lb_target_group.cible : g.protocol == "HTTPS" && g.port == 8443 && g.health_check[0].protocol == "HTTPS"])
    error_message = "Les cibles sont jointes en HTTPS (Kestrel, PKI interne)."
  }

  assert {
    condition     = aws_vpc_security_group_ingress_rule.https_depuis_vpc.cidr_ipv4 == "10.20.0.0/16"
    error_message = "Seul le VPC atteint l'équilibreur."
  }
}

run "lectures_vers_la_requete_ecritures_vers_les_commandes" {
  command = apply

  assert {
    condition     = toset(flatten([for c in aws_lb_listener_rule.lectures.condition : [for m in c.http_request_method : m.values]])) == toset(["GET", "HEAD"])
    error_message = "GET et HEAD vont à l'API de requête."
  }

  assert {
    condition     = aws_lb_listener.https.default_action[0].target_group_arn == aws_lb_target_group.cible["command-api"].arn
    error_message = "Les autres méthodes vont à l'API de commandes."
  }

  assert {
    condition     = output.nom_hote == "api.ledger-dev.internal"
    error_message = "L'API est publiée sous api.<domaine>."
  }
}
