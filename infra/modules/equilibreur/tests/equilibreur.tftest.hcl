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
      domain_validation_options = [{
        domain_name           = "api.dev.aws.usinageone.ca"
        resource_record_name  = "_abc.api.dev.aws.usinageone.ca."
        resource_record_type  = "CNAME"
        resource_record_value = "_def.acm-validations.aws."
      }]
    }
  }

  mock_resource "aws_acm_certificate_validation" {
    defaults = {
      certificate_arn = "arn:aws:acm:ca-central-1:111122223333:certificate/abc"
    }
  }
}

variables {
  prefixe       = "ledger-dev"
  vpc_id        = "vpc-0123456789abcdef0"
  sous_reseaux  = ["subnet-0000000000000001a", "subnet-0000000000000001b", "subnet-0000000000000001c"]
  zone_publique = { id = "Z0123456789ABC", domaine = "dev.aws.usinageone.ca" }
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

}

run "certificat_public_valide_par_dns" {
  command = apply

  assert {
    condition     = aws_acm_certificate.api.domain_name == "api.dev.aws.usinageone.ca" && aws_acm_certificate.api.validation_method == "DNS"
    error_message = "Certificat ACM public de api.<zone publique>, validé par DNS (ADR 0031)."
  }

  assert {
    condition     = aws_acm_certificate.api.private_key == null && aws_acm_certificate.api.certificate_body == null
    error_message = "Aucun certificat importé : ACM émet et renouvelle le certificat."
  }

  assert {
    condition     = aws_route53_record.validation.zone_id == "Z0123456789ABC" && aws_route53_record.validation.type == "CNAME" && aws_route53_record.validation.name == "_abc.api.dev.aws.usinageone.ca."
    error_message = "Seul l'enregistrement de validation est publié, dans la zone publique du compte."
  }

  assert {
    condition     = aws_lb_listener.https.certificate_arn == aws_acm_certificate_validation.api.certificate_arn
    error_message = "L'écouteur attend le certificat validé."
  }
}

run "aucune_entree_sans_api_gateway" {
  command = apply

  assert {
    condition     = length(aws_security_group.equilibreur.ingress) == 0
    error_message = "Aucune entrée posée par le module : seul le VPC Link d'API Gateway en ouvre une."
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
    condition     = output.nom_hote == "api.dev.aws.usinageone.ca"
    error_message = "L'intégration d'API Gateway vise api.<zone publique>, nom du certificat."
  }
}
