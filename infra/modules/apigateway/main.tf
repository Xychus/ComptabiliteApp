# Entrée de l'API (ADR 0031) : API REST privée joignable seulement par le point de terminaison
# execute-api du VPC, autorizer Lambda REQUEST (Ledger.Authorizer, runtime géré dotnet10) qui
# valide le JWT Keycloak et désigne la clé du plan d'usage (api_key_source = AUTHORIZER),
# intégration HTTPS par VPC Link V2 vers l'équilibreur interne (integration_target), plans
# d'usage par catégorie de clients avec un débit distinct en lecture et en écriture. Cache
# d'API Gateway désactivé : une réponse dépend de l'autorisation de l'appelant.

locals {
  nom   = "${var.prefixe}-api"
  etape = "api"

  # Une méthode par verbe : le débit des plans d'usage se règle par chemin et par méthode, et
  # chaque verbe relève d'un seul scope (PolitiqueRoutes de l'autorizer).
  methodes = {
    GET  = "lecture"
    HEAD = "lecture"
    PUT  = "ecriture"
    POST = "ecriture"
  }

  clients = merge([
    for categorie, plan in var.plans_usage : { for client in plan.clients : client => categorie }
  ]...)

  prefixe_cles = "/${var.prefixe}/apigateway/cles-plan-usage"
}

# --- Autorizer -----------------------------------------------------------------------------

resource "aws_iam_role" "autorizer" {
  name                 = "${local.nom}-autorizer"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
      Condition = { StringEquals = { "aws:SourceAccount" = var.compte_aws } }
    }]
  })
}

resource "aws_iam_role_policy" "autorizer" {
  #checkov:skip=CKV_AWS_355:Interfaces réseau et traces X-Ray : actions sans ressource précise (documentation Lambda) ; le reste est limité à ses ressources.
  #checkov:skip=CKV_AWS_290:Seules écritures sans ressource précise : interfaces réseau de la Lambda dans le VPC et segments X-Ray.
  name = "autorizer"
  role = aws_iam_role.autorizer.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "Journaux"
        Effect   = "Allow"
        Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
        Resource = "${aws_cloudwatch_log_group.autorizer.arn}:*"
      },
      # Interfaces réseau de la Lambda dans le VPC : actions sans ressource précise (documentation Lambda).
      {
        Sid    = "InterfacesReseau"
        Effect = "Allow"
        Action = [
          "ec2:AssignPrivateIpAddresses", "ec2:CreateNetworkInterface", "ec2:DeleteNetworkInterface",
          "ec2:DescribeNetworkInterfaces", "ec2:UnassignPrivateIpAddresses",
        ]
        Resource = "*"
      },
      {
        Sid      = "Traces"
        Effect   = "Allow"
        Action   = ["xray:PutTelemetryRecords", "xray:PutTraceSegments"]
        Resource = "*"
      },
      {
        Sid       = "VariablesChiffrees"
        Effect    = "Allow"
        Action    = "kms:Decrypt"
        Resource  = var.cle_kms_lambda_arn
        Condition = { StringEquals = { "kms:ViaService" = "lambda.${var.region}.amazonaws.com" } }
      },
      {
        Sid      = "ClesPlansUsage"
        Effect   = "Allow"
        Action   = "ssm:GetParameter"
        Resource = "arn:aws:ssm:${var.region}:${var.compte_aws}:parameter${local.prefixe_cles}/*"
      },
      {
        Sid       = "DechiffrementClesPlansUsage"
        Effect    = "Allow"
        Action    = "kms:Decrypt"
        Resource  = var.cle_kms_ssm_arn
        Condition = { StringEquals = { "kms:ViaService" = "ssm.${var.region}.amazonaws.com" } }
      },
    ]
  })
}

resource "aws_cloudwatch_log_group" "autorizer" {
  name              = "/aws/lambda/${local.nom}-autorizer"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

resource "aws_security_group" "autorizer" {
  name        = "${local.nom}-autorizer"
  description = "Autorizer : HTTPS vers Keycloak (OIDC) et vers les points de terminaison VPC"
  vpc_id      = var.vpc_id

  tags = {
    Name = "${local.nom}-autorizer"
  }
}

resource "aws_vpc_security_group_egress_rule" "autorizer_vers_points_de_terminaison" {
  security_group_id            = aws_security_group.autorizer.id
  description                  = "HTTPS vers les points de terminaison VPC (SSM)"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = var.groupe_securite_points_de_terminaison
}

# Keycloak accepte déjà le VPC (module keycloak) : seule la sortie est déclarée ici.
resource "aws_vpc_security_group_egress_rule" "autorizer_vers_keycloak" {
  security_group_id            = aws_security_group.autorizer.id
  description                  = "HTTPS vers Keycloak : configuration OIDC et JWKS du realm"
  ip_protocol                  = "tcp"
  from_port                    = var.keycloak.port
  to_port                      = var.keycloak.port
  referenced_security_group_id = var.keycloak.groupe_securite
}

resource "aws_lambda_function" "autorizer" {
  #checkov:skip=CKV_AWS_116:Invocation synchrone par API Gateway : un échec est rendu à l'appelant (401/500) ; une file de lettres mortes n'ajouterait rien.
  #checkov:skip=CKV_AWS_272:Signature des paquets : phase 10 (deploy.yml), avec la signature des images.
  #checkov:skip=CKV_AWS_115:Débit borné en amont par le throttling de l'étape et des plans d'usage ; une réserve retirerait de la concurrence aux autres Lambda du compte.
  function_name = "${local.nom}-autorizer"
  description   = "Autorizer de l'API privee : JWT Keycloak, scopes, cle du plan d'usage (ADR 0031)"
  role          = aws_iam_role.autorizer.arn
  runtime       = "dotnet10"
  handler       = "Ledger.Authorizer"
  architectures = ["x86_64"]
  s3_bucket     = var.paquet.bucket
  s3_key        = "authorizer/${var.paquet.version}.zip"
  memory_size   = 512
  timeout       = 10

  # Paquet et variables chiffrés par la CMK « lambda » (jamais une clé gérée par AWS).
  kms_key_arn        = var.cle_kms_lambda_arn
  source_kms_key_arn = var.cle_kms_lambda_arn

  environment {
    variables = merge(
      {
        DOTNET_EnableDiagnostics       = "0"
        LEDGER_EMETTEUR                = var.keycloak.url_realm
        LEDGER_AUDIENCE                = var.audience
        LEDGER_PREFIXE_CLES_PLAN_USAGE = local.prefixe_cles
      },
      var.certificat_racine_pki_pem != null ? { LEDGER_RACINE_PKI = var.certificat_racine_pki_pem } : {},
    )
  }

  vpc_config {
    subnet_ids         = var.sous_reseaux
    security_group_ids = [aws_security_group.autorizer.id]
  }

  tracing_config {
    mode = "Active"
  }

  logging_config {
    log_format = "Text"
    log_group  = aws_cloudwatch_log_group.autorizer.name
  }

  depends_on = [aws_iam_role_policy.autorizer]
}

resource "aws_lambda_permission" "api_gateway" {
  statement_id  = "AutorizerApiGateway"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.autorizer.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_api_gateway_rest_api.api.execution_arn}/authorizers/${aws_api_gateway_authorizer.jwt.id}"
}

# --- VPC Link V2 vers l'équilibreur ----------------------------------------------------------

resource "aws_security_group" "lien_vpc" {
  name        = "${local.nom}-lien-vpc"
  description = "VPC Link d API Gateway : HTTPS vers l equilibreur interne seulement"
  vpc_id      = var.vpc_id

  tags = {
    Name = "${local.nom}-lien-vpc"
  }
}

resource "aws_vpc_security_group_egress_rule" "lien_vers_equilibreur" {
  security_group_id            = aws_security_group.lien_vpc.id
  description                  = "HTTPS vers l equilibreur interne"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = var.equilibreur.groupe_securite
}

# Seule entrée de l'équilibreur : toute requête passe par API Gateway (ADR 0031, point 7).
resource "aws_vpc_security_group_ingress_rule" "equilibreur_depuis_lien" {
  security_group_id            = var.equilibreur.groupe_securite
  description                  = "HTTPS depuis le VPC Link d API Gateway"
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
  referenced_security_group_id = aws_security_group.lien_vpc.id
}

resource "aws_apigatewayv2_vpc_link" "equilibreur" {
  name               = "${local.nom}-equilibreur"
  subnet_ids         = var.sous_reseaux
  security_group_ids = [aws_security_group.lien_vpc.id]
}

# --- API REST privée -------------------------------------------------------------------------

resource "aws_api_gateway_rest_api" "api" {
  name           = local.nom
  description    = "Grand livre : API privee (lectures et ecritures), JWT Keycloak"
  api_key_source = "AUTHORIZER"

  # TLS 1.3 seulement : clients .NET 10 internes seulement (CLAUDE.md). STRICT : le nom SNI doit
  # être celui de l'API (DNS privé du point de terminaison execute-api).
  security_policy      = "SecurityPolicy_TLS13_1_3_2025_09"
  endpoint_access_mode = "STRICT"

  endpoint_configuration {
    types = ["PRIVATE"]
  }

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "InvocationDepuisLeVpc"
        Effect    = "Allow"
        Principal = "*"
        Action    = "execute-api:Invoke"
        Resource  = "execute-api:/*"
      },
      {
        Sid       = "AutrePointDeTerminaisonRefuse"
        Effect    = "Deny"
        Principal = "*"
        Action    = "execute-api:Invoke"
        Resource  = "execute-api:/*"
        Condition = { StringNotEquals = { "aws:SourceVpce" = var.point_de_terminaison_execute_api } }
      },
    ]
  })

  lifecycle {
    create_before_destroy = true
  }
}

resource "aws_api_gateway_authorizer" "jwt" {
  name                             = "jwt-keycloak"
  rest_api_id                      = aws_api_gateway_rest_api.api.id
  type                             = "REQUEST"
  authorizer_uri                   = aws_lambda_function.autorizer.invoke_arn
  identity_source                  = "method.request.header.Authorization"
  authorizer_result_ttl_in_seconds = var.duree_cache_autorisation_secondes
}

# Le corps est validé par les API (contrat OpenAPI, JSON strict) ; la passerelle vérifie le chemin.
resource "aws_api_gateway_request_validator" "parametres" {
  name                        = "parametres"
  rest_api_id                 = aws_api_gateway_rest_api.api.id
  validate_request_parameters = true
  validate_request_body       = false
}

resource "aws_api_gateway_resource" "mandataire" {
  rest_api_id = aws_api_gateway_rest_api.api.id
  parent_id   = aws_api_gateway_rest_api.api.root_resource_id
  path_part   = "{proxy+}"
}

resource "aws_api_gateway_method" "methode" {
  for_each = local.methodes

  rest_api_id          = aws_api_gateway_rest_api.api.id
  resource_id          = aws_api_gateway_resource.mandataire.id
  http_method          = each.key
  authorization        = "CUSTOM"
  authorizer_id        = aws_api_gateway_authorizer.jwt.id
  api_key_required     = true
  request_validator_id = aws_api_gateway_request_validator.parametres.id

  request_parameters = {
    "method.request.path.proxy" = true
  }
}

# Le nom de l'URI sert d'en-tête Host et de nom vérifié dans le certificat public de l'équilibreur ;
# la connexion vise l'équilibreur lui-même (integration_target), sans résolution DNS publique.
resource "aws_api_gateway_integration" "equilibreur" {
  for_each = local.methodes

  rest_api_id             = aws_api_gateway_rest_api.api.id
  resource_id             = aws_api_gateway_resource.mandataire.id
  http_method             = aws_api_gateway_method.methode[each.key].http_method
  type                    = "HTTP_PROXY"
  integration_http_method = each.key
  uri                     = "https://${var.equilibreur.nom_hote}/{proxy}"
  connection_type         = "VPC_LINK"
  connection_id           = aws_apigatewayv2_vpc_link.equilibreur.id
  integration_target      = var.equilibreur.arn
  timeout_milliseconds    = 29000

  request_parameters = {
    "integration.request.path.proxy" = "method.request.path.proxy"
  }
}

resource "aws_api_gateway_deployment" "api" {
  rest_api_id = aws_api_gateway_rest_api.api.id

  triggers = {
    redeploiement = sha1(jsonencode([
      aws_api_gateway_rest_api.api.policy,
      aws_api_gateway_authorizer.jwt,
      aws_api_gateway_request_validator.parametres,
      aws_api_gateway_resource.mandataire.id,
      aws_api_gateway_method.methode,
      aws_api_gateway_integration.equilibreur,
    ]))
  }

  lifecycle {
    create_before_destroy = true
  }
}

# --- Étape, journaux --------------------------------------------------------------------------

# Rôle de journalisation d'API Gateway : réglage du compte et de la région (un environnement par compte).
resource "aws_iam_role" "journalisation" {
  name                 = "${local.nom}-journalisation"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "apigateway.amazonaws.com" }
      Action    = "sts:AssumeRole"
      Condition = { StringEquals = { "aws:SourceAccount" = var.compte_aws } }
    }]
  })
}

resource "aws_iam_role_policy" "journalisation" {
  name = "journalisation"
  role = aws_iam_role.journalisation.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "GroupesDeLApi"
        Effect   = "Allow"
        Action   = ["logs:CreateLogGroup", "logs:CreateLogStream", "logs:DescribeLogStreams", "logs:PutLogEvents"]
        Resource = [for g in [aws_cloudwatch_log_group.acces, aws_cloudwatch_log_group.execution] : "${g.arn}:*"]
      },
      {
        Sid      = "InventaireGroupes"
        Effect   = "Allow"
        Action   = "logs:DescribeLogGroups"
        Resource = "*"
      },
    ]
  })
}

resource "aws_api_gateway_account" "compte" {
  cloudwatch_role_arn = aws_iam_role.journalisation.arn

  depends_on = [aws_iam_role_policy.journalisation]
}

resource "aws_cloudwatch_log_group" "acces" {
  name              = "/aws/apigateway/${local.nom}/acces"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

# Nom imposé par API Gateway ; créé ici pour porter la CMK et la conservation.
resource "aws_cloudwatch_log_group" "execution" {
  name              = "API-Gateway-Execution-Logs_${aws_api_gateway_rest_api.api.id}/${local.etape}"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

resource "aws_api_gateway_stage" "api" {
  #checkov:skip=CKV2_AWS_29:WAF à revoir en phase 11 : API privée, appelants authentifiés par l'autorizer et limités par les plans d'usage.
  #checkov:skip=CKV2_AWS_51:Certificat client vers l'intégration inutile : l'équilibreur n'accepte que le VPC Link et les API valident elles-mêmes le JWT (ADR 0014).
  #checkov:skip=CKV_AWS_120:Cache désactivé à dessein (ADR 0031) : une réponse dépend de l'autorisation de l'appelant.
  rest_api_id           = aws_api_gateway_rest_api.api.id
  deployment_id         = aws_api_gateway_deployment.api.id
  stage_name            = local.etape
  cache_cluster_enabled = false
  xray_tracing_enabled  = true

  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.acces.arn
    format = jsonencode({
      requestId          = "$context.requestId"
      requestTime        = "$context.requestTimeEpoch"
      sourceVpce         = "$context.identity.vpceId"
      sourceIp           = "$context.identity.sourceIp"
      clientId           = "$context.authorizer.clientId"
      httpMethod         = "$context.httpMethod"
      path               = "$context.path"
      status             = "$context.status"
      errorType          = "$context.error.responseType"
      authorizerStatus   = "$context.authorizer.status"
      authorizerLatency  = "$context.authorizer.latency"
      integrationStatus  = "$context.integration.status"
      integrationLatency = "$context.integration.latency"
      responseLatency    = "$context.responseLatency"
    })
  }

  depends_on = [aws_api_gateway_account.compte, aws_cloudwatch_log_group.execution]
}

resource "aws_api_gateway_method_settings" "toutes" {
  #checkov:skip=CKV_AWS_225:Cache désactivé à dessein (ADR 0031) : une réponse dépend de l'autorisation de l'appelant.
  rest_api_id = aws_api_gateway_rest_api.api.id
  stage_name  = aws_api_gateway_stage.api.stage_name
  method_path = "*/*"

  settings {
    metrics_enabled        = true
    logging_level          = "ERROR"
    data_trace_enabled     = false
    caching_enabled        = false
    cache_data_encrypted   = true
    throttling_rate_limit  = var.debit_etape.debit
    throttling_burst_limit = var.debit_etape.rafale
  }
}

# --- Plans d'usage ----------------------------------------------------------------------------

resource "aws_api_gateway_usage_plan" "categorie" {
  for_each = var.plans_usage

  name        = "${local.nom}-${each.key}"
  description = "Clients ${each.key} : debit distinct en lecture et en ecriture"

  api_stages {
    api_id = aws_api_gateway_rest_api.api.id
    stage  = aws_api_gateway_stage.api.stage_name

    dynamic "throttle" {
      for_each = local.methodes
      content {
        path        = "/${aws_api_gateway_resource.mandataire.path_part}/${throttle.key}"
        rate_limit  = each.value[throttle.value].debit
        burst_limit = each.value[throttle.value].rafale
      }
    }
  }

  # Plafond global du plan : somme des débits de lecture et d'écriture.
  throttle_settings {
    rate_limit  = each.value.lecture.debit + each.value.ecriture.debit
    burst_limit = each.value.lecture.rafale + each.value.ecriture.rafale
  }

  dynamic "quota_settings" {
    for_each = each.value.quota_quotidien != null ? [each.value.quota_quotidien] : []
    content {
      limit  = quota_settings.value
      period = "DAY"
    }
  }
}

# La clé API n'authentifie personne (aucun client ne la détient) : elle compte le débit d'un azp
# déjà authentifié. Valeur générée par API Gateway, recopiée dans SSM pour l'autorizer (ADR 0031).
resource "aws_api_gateway_api_key" "client" {
  for_each = local.clients

  name        = "${local.nom}-${each.key}"
  description = "Plan d'usage ${each.value} du client ${each.key}"
  enabled     = true
}

resource "aws_api_gateway_usage_plan_key" "client" {
  for_each = local.clients

  key_id        = aws_api_gateway_api_key.client[each.key].id
  key_type      = "API_KEY"
  usage_plan_id = aws_api_gateway_usage_plan.categorie[each.value].id
}

resource "aws_ssm_parameter" "cle_plan_usage" {
  for_each = local.clients

  name        = "${local.prefixe_cles}/${each.key}"
  description = "Cle du plan d'usage de ${each.key} (lue par l'autorizer)"
  type        = "SecureString"
  key_id      = var.cle_kms_ssm_arn
  value       = aws_api_gateway_api_key.client[each.key].value
}

# --- Alarmes (sans action : sujet SNS en phase 11) -----------------------------------------
# Les erreurs de la Lambda ne sont pas alarmées : chaque refus « Unauthorized » (401) en est une
# pour Lambda. Un échec réel de l'autorizer devient une 500 d'API Gateway, alarmée ici.

resource "aws_cloudwatch_metric_alarm" "erreurs_5xx" {
  alarm_name          = "${local.nom}-passerelle-5xx"
  alarm_description   = "Plus de 10 reponses 5xx d API Gateway en 5 minutes (autorizer ou integration)."
  namespace           = "AWS/ApiGateway"
  metric_name         = "5XXError"
  dimensions          = { ApiName = aws_api_gateway_rest_api.api.name, Stage = aws_api_gateway_stage.api.stage_name }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  comparison_operator = "GreaterThanThreshold"
  threshold           = 10
  treat_missing_data  = "notBreaching"
}
