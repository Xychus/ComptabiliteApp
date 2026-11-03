# Équilibreur de charge applicatif interne (phase 8, point P2 tranché en phase 9) : HTTPS 443,
# politique TLS 1.3 privilégiée (1.2 au minimum), certificat de la PKI interne importé dans ACM et
# réimporté sous le même ARN à chaque rotation par la Lambda PKI (ADR 0010). Cibles en HTTPS vers
# Kestrel (8443). Routage par méthode : lectures (GET, HEAD) vers l'API de requête, écritures vers
# l'API de commandes ; les chemins /v1 des deux contrats se recouvrent (ADR 0014, 0019).

locals {
  nom      = "${var.prefixe}-api"
  nom_hote = "api.${var.zone_interne.domaine}"
}

# Certificat de démarrage, autosigné, remplacé par celui de la PKI à la première rotation (la Lambda
# réimporte sous le même ARN ; Terraform ignore ensuite le contenu). Aucun client ne lui fait confiance.
resource "tls_private_key" "demarrage" {
  algorithm   = "ECDSA"
  ecdsa_curve = "P256"
}

resource "tls_self_signed_cert" "demarrage" {
  private_key_pem       = tls_private_key.demarrage.private_key_pem
  validity_period_hours = 720
  allowed_uses          = ["digital_signature", "server_auth"]
  dns_names             = [local.nom_hote]

  subject {
    common_name = "api"
  }
}

resource "aws_acm_certificate" "api" {
  private_key      = tls_private_key.demarrage.private_key_pem
  certificate_body = tls_self_signed_cert.demarrage.cert_pem

  tags = {
    Name = local.nom
  }

  lifecycle {
    create_before_destroy = true
    ignore_changes        = [private_key, certificate_body, certificate_chain]
  }
}

resource "aws_security_group" "equilibreur" {
  name        = local.nom
  description = "Equilibreur interne : HTTPS depuis le VPC, HTTPS vers les services"
  vpc_id      = var.vpc_id

  tags = {
    Name = local.nom
  }
}

resource "aws_vpc_security_group_ingress_rule" "https_depuis_vpc" {
  security_group_id = aws_security_group.equilibreur.id
  description       = "HTTPS depuis le VPC"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  cidr_ipv4         = var.cidr_vpc
}

resource "aws_lb" "api" {
  #checkov:skip=CKV_AWS_91:Les journaux d'accès d'un ALB n'acceptent que le chiffrement SSE-S3 (clé gérée par AWS), interdit par CLAUDE.md ; traces X-Ray et journaux des services en tiennent lieu (à revoir en phase 11).
  #checkov:skip=CKV2_AWS_28:Équilibreur interne, derrière API Gateway privée (phase 9) : WAF porté par API Gateway.
  #checkov:skip=CKV2_AWS_20:Aucun écouteur HTTP : l'équilibreur n'écoute qu'en HTTPS.
  name                       = local.nom
  internal                   = true
  load_balancer_type         = "application"
  subnets                    = var.sous_reseaux
  security_groups            = [aws_security_group.equilibreur.id]
  drop_invalid_header_fields = true
  enable_deletion_protection = var.protection_suppression
}

resource "aws_lb_target_group" "cible" {
  for_each = toset(["command-api", "query-api"])

  name                 = "${var.prefixe}-${each.key}"
  port                 = var.port_cibles
  protocol             = "HTTPS"
  target_type          = "ip"
  vpc_id               = var.vpc_id
  deregistration_delay = 30

  health_check {
    protocol            = "HTTPS"
    path                = var.chemin_sante
    matcher             = "200"
    interval            = 15
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_lb_listener" "https" {
  load_balancer_arn = aws_lb.api.arn
  port              = 443
  protocol          = "HTTPS"
  # TLS 1.3 négocié en priorité, 1.2 accepté : les clients d'API Gateway (VPC Link, phase 9) ne
  # garantissent pas TLS 1.3 vers une cible privée (CLAUDE.md, plancher justifié).
  ssl_policy      = "ELBSecurityPolicy-TLS13-1-2-Res-2021-06"
  certificate_arn = aws_acm_certificate.api.arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.cible["command-api"].arn
  }
}

resource "aws_lb_listener_rule" "lectures" {
  listener_arn = aws_lb_listener.https.arn
  priority     = 10

  condition {
    http_request_method {
      values = ["GET", "HEAD"]
    }
  }

  action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.cible["query-api"].arn
  }
}

resource "aws_route53_record" "api" {
  zone_id = var.zone_interne.id
  name    = local.nom_hote
  type    = "A"

  alias {
    name                   = aws_lb.api.dns_name
    zone_id                = aws_lb.api.zone_id
    evaluate_target_health = true
  }
}

# --- Alarmes (sans action : sujet SNS en phase 11) -----------------------------------------

resource "aws_cloudwatch_metric_alarm" "cibles_en_echec" {
  for_each = aws_lb_target_group.cible

  alarm_name          = "${var.prefixe}-${each.key}-cibles-en-echec"
  alarm_description   = "Au moins une cible de ${each.key} echoue a la sonde de disponibilite."
  namespace           = "AWS/ApplicationELB"
  metric_name         = "UnHealthyHostCount"
  dimensions          = { LoadBalancer = aws_lb.api.arn_suffix, TargetGroup = each.value.arn_suffix }
  statistic           = "Maximum"
  period              = 60
  evaluation_periods  = 5
  comparison_operator = "GreaterThanThreshold"
  threshold           = 0
  treat_missing_data  = "notBreaching"
}

resource "aws_cloudwatch_metric_alarm" "erreurs_5xx" {
  alarm_name          = "${local.nom}-erreurs-5xx"
  alarm_description   = "Plus de 10 reponses 5xx des services en 5 minutes."
  namespace           = "AWS/ApplicationELB"
  metric_name         = "HTTPCode_Target_5XX_Count"
  dimensions          = { LoadBalancer = aws_lb.api.arn_suffix }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  comparison_operator = "GreaterThanThreshold"
  threshold           = 10
  treat_missing_data  = "notBreaching"
}
