# Équilibreur de charge applicatif interne (phase 8, ADR 0031) : HTTPS 443, politique TLS 1.3
# privilégiée (1.2 au minimum), certificat ACM public validé par DNS dans la zone publique du
# compte : API Gateway n'accepte vers une intégration privée qu'un certificat d'autorité publique.
# Seul le VPC Link d'API Gateway y entre (règle posée par le module apigateway). Cibles en HTTPS
# vers Kestrel (8443, PKI interne). Routage par méthode : lectures (GET, HEAD) vers l'API de
# requête, écritures vers l'API de commandes ; les chemins /v1 des deux contrats se recouvrent
# (ADR 0014, 0019).

locals {
  nom      = "${var.prefixe}-api"
  nom_hote = "api.${var.zone_publique.domaine}"
}

# Seul l'enregistrement de validation est publié dans la zone publique : aucun enregistrement
# public ne désigne l'équilibreur, joint par API Gateway à travers le VPC Link (integration_target).
resource "aws_acm_certificate" "api" {
  domain_name       = local.nom_hote
  validation_method = "DNS"
  key_algorithm     = "EC_prime256v1"

  options {
    certificate_transparency_logging_preference = "ENABLED"
  }

  tags = {
    Name = local.nom
  }

  lifecycle {
    create_before_destroy = true
  }
}

# Un seul nom dans le certificat : une seule option de validation, connue seulement à l'apply.
locals {
  validation = one(aws_acm_certificate.api.domain_validation_options)
}

resource "aws_route53_record" "validation" {
  zone_id         = var.zone_publique.id
  name            = local.validation.resource_record_name
  type            = local.validation.resource_record_type
  records         = [local.validation.resource_record_value]
  ttl             = 300
  allow_overwrite = true
}

# Attend l'émission : exige la délégation de la zone publique (runbook phase-09-point-d-arret).
resource "aws_acm_certificate_validation" "api" {
  certificate_arn         = aws_acm_certificate.api.arn
  validation_record_fqdns = [aws_route53_record.validation.fqdn]
}

resource "aws_security_group" "equilibreur" {
  name        = local.nom
  description = "Equilibreur interne : HTTPS depuis le VPC Link d API Gateway, HTTPS vers les services"
  vpc_id      = var.vpc_id

  tags = {
    Name = local.nom
  }
}

resource "aws_lb" "api" {
  #checkov:skip=CKV_AWS_91:Les journaux d'accès d'un ALB n'acceptent que le chiffrement SSE-S3 (clé gérée par AWS), interdit par CLAUDE.md ; traces X-Ray et journaux des services en tiennent lieu (à revoir en phase 11).
  #checkov:skip=CKV2_AWS_28:Équilibreur interne joint seulement par le VPC Link d'API Gateway privée (ADR 0031) ; WAF à revoir en phase 11.
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
  certificate_arn = aws_acm_certificate_validation.api.certificate_arn

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
