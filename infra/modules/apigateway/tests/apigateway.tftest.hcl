# Fournisseur simulé : aucun appel AWS, aucune clé réelle.
mock_provider "aws" {
  mock_resource "aws_api_gateway_rest_api" {
    defaults = {
      id               = "abc123defg"
      root_resource_id = "racine0001"
      execution_arn    = "arn:aws:execute-api:ca-central-1:111122223333:abc123defg"
    }
  }

  mock_resource "aws_lambda_function" {
    defaults = {
      arn        = "arn:aws:lambda:ca-central-1:111122223333:function:ledger-dev-api-autorizer"
      invoke_arn = "arn:aws:apigateway:ca-central-1:lambda:path/2015-03-31/functions/arn:aws:lambda:ca-central-1:111122223333:function:ledger-dev-api-autorizer/invocations"
    }
  }

  mock_resource "aws_cloudwatch_log_group" {
    defaults = {
      arn = "arn:aws:logs:ca-central-1:111122223333:log-group:ledger-dev-api"
    }
  }

  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/ledger/ledger-dev-api"
    }
  }

  mock_resource "aws_api_gateway_api_key" {
    defaults = {
      value = "cle-generee-par-api-gateway-0123456789"
    }
  }
}

variables {
  prefixe                               = "ledger-dev"
  region                                = "ca-central-1"
  compte_aws                            = "111122223333"
  vpc_id                                = "vpc-0123456789abcdef0"
  sous_reseaux                          = ["subnet-0000000000000001a", "subnet-0000000000000001b", "subnet-0000000000000001c"]
  groupe_securite_points_de_terminaison = "sg-0000000000000000e"
  point_de_terminaison_execute_api      = "vpce-0123456789abcdef0"
  certificat_racine_pki_pem             = "-----BEGIN CERTIFICATE-----\nracine\n-----END CERTIFICATE-----\n"
  cle_kms_journaux_arn                  = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-1111-4a5b-9c3d-0123456789ab"
  cle_kms_lambda_arn                    = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-7777-4a5b-9c3d-0123456789ab"
  cle_kms_ssm_arn                       = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-8888-4a5b-9c3d-0123456789ab"
  frontiere_permissions_arn             = "arn:aws:iam::111122223333:policy/bootstrap/ledger-frontiere-charges"

  equilibreur = {
    arn             = "arn:aws:elasticloadbalancing:ca-central-1:111122223333:loadbalancer/app/ledger-dev-api/abc"
    groupe_securite = "sg-000000000000000a1"
    nom_hote        = "api.dev.aws.usinageone.ca"
  }

  keycloak = {
    url_realm       = "https://keycloak.idp.ledger-dev.internal:8443/realms/ledger"
    groupe_securite = "sg-000000000000000c1"
    port            = 8443
  }

  paquet = {
    bucket  = "ledger-dev-artefacts-111122223333-ca-central-1"
    version = "0.1.0"
  }

  plans_usage = {
    standard = {
      clients  = ["ledger-exemple-ecriture", "ledger-exemple-lecture"]
      lecture  = { debit = 50, rafale = 100 }
      ecriture = { debit = 10, rafale = 20 }
    }
    lot = {
      clients         = ["ledger-import"]
      lecture         = { debit = 5, rafale = 10 }
      ecriture        = { debit = 100, rafale = 200 }
      quota_quotidien = 500000
    }
  }
}

run "api_privee_limitee_au_point_de_terminaison" {
  command = apply

  assert {
    condition     = aws_api_gateway_rest_api.api.endpoint_configuration[0].types == tolist(["PRIVATE"]) && aws_api_gateway_rest_api.api.api_key_source == "AUTHORIZER"
    error_message = "API privée ; clé du plan d'usage fournie par l'autorizer."
  }

  assert {
    condition     = aws_api_gateway_rest_api.api.security_policy == "SecurityPolicy_TLS13_1_3_2025_09" && aws_api_gateway_rest_api.api.endpoint_access_mode == "STRICT"
    error_message = "TLS 1.3 seulement, accès strict."
  }

  assert {
    condition     = one([for s in jsondecode(aws_api_gateway_rest_api.api.policy).Statement : s.Condition.StringNotEquals["aws:SourceVpce"] if s.Effect == "Deny"]) == "vpce-0123456789abcdef0"
    error_message = "Toute invocation hors du point de terminaison execute-api du VPC est refusée."
  }
}

run "toutes_les_methodes_derriere_l_autorizer" {
  command = apply

  assert {
    condition     = toset(keys(aws_api_gateway_method.methode)) == toset(["GET", "HEAD", "PUT", "POST"])
    error_message = "Une méthode par verbe du contrat (débit distinct par méthode)."
  }

  assert {
    condition     = alltrue([for m in aws_api_gateway_method.methode : m.authorization == "CUSTOM" && m.authorizer_id == aws_api_gateway_authorizer.jwt.id && m.api_key_required])
    error_message = "Chaque méthode exige l'autorizer et une clé de plan d'usage."
  }

  assert {
    condition     = aws_api_gateway_authorizer.jwt.type == "REQUEST" && aws_api_gateway_authorizer.jwt.identity_source == "method.request.header.Authorization" && aws_api_gateway_authorizer.jwt.authorizer_result_ttl_in_seconds == 300
    error_message = "Autorizer REQUEST sur l'en-tête Authorization, politique gardée au plus 300 s."
  }

  assert {
    condition     = aws_lambda_permission.api_gateway.source_arn == "arn:aws:execute-api:ca-central-1:111122223333:abc123defg/authorizers/${aws_api_gateway_authorizer.jwt.id}"
    error_message = "Seul l'autorizer de cette API invoque la Lambda."
  }
}

run "autorizer_runtime_gere_paquet_et_variables_chiffres" {
  command = apply

  assert {
    condition     = aws_lambda_function.autorizer.runtime == "dotnet10" && aws_lambda_function.autorizer.s3_key == "authorizer/0.1.0.zip" && aws_lambda_function.autorizer.s3_bucket == var.paquet.bucket
    error_message = "Runtime géré dotnet10, paquet de la version exacte publiée par images.yml."
  }

  assert {
    condition     = aws_lambda_function.autorizer.kms_key_arn == var.cle_kms_lambda_arn && aws_lambda_function.autorizer.source_kms_key_arn == var.cle_kms_lambda_arn
    error_message = "Paquet et variables chiffrés par la CMK « lambda »."
  }

  assert {
    condition = (aws_lambda_function.autorizer.environment[0].variables["LEDGER_EMETTEUR"] == var.keycloak.url_realm
      && aws_lambda_function.autorizer.environment[0].variables["LEDGER_PREFIXE_CLES_PLAN_USAGE"] == "/ledger-dev/apigateway/cles-plan-usage"
    && aws_lambda_function.autorizer.environment[0].variables["LEDGER_RACINE_PKI"] == var.certificat_racine_pki_pem)
    error_message = "Émetteur, préfixe des clés et racine de la PKI transmis à l'autorizer."
  }

  assert {
    condition     = one([for s in jsondecode(aws_iam_role_policy.autorizer.policy).Statement : s.Resource if s.Sid == "ClesPlansUsage"]) == "arn:aws:ssm:ca-central-1:111122223333:parameter/ledger-dev/apigateway/cles-plan-usage/*"
    error_message = "L'autorizer ne lit que les clés des plans d'usage."
  }

  assert {
    condition     = aws_iam_role.autorizer.path == "/ledger/" && aws_iam_role.autorizer.permissions_boundary == var.frontiere_permissions_arn
    error_message = "Rôle sous /ledger/ avec la frontière de permissions."
  }

  assert {
    condition     = aws_vpc_security_group_egress_rule.autorizer_vers_keycloak.referenced_security_group_id == "sg-000000000000000c1" && aws_vpc_security_group_egress_rule.autorizer_vers_keycloak.from_port == 8443
    error_message = "L'autorizer joint Keycloak en HTTPS."
  }
}

run "integration_privee_vers_l_equilibreur" {
  command = apply

  assert {
    condition = alltrue([for i in aws_api_gateway_integration.equilibreur : (
      i.type == "HTTP_PROXY" && i.connection_type == "VPC_LINK" && i.connection_id == aws_apigatewayv2_vpc_link.equilibreur.id
      && i.integration_target == var.equilibreur.arn && i.uri == "https://api.dev.aws.usinageone.ca/{proxy}"
    )])
    error_message = "Intégration HTTPS par le VPC Link V2, vers l'équilibreur, sous le nom de son certificat public."
  }

  assert {
    condition     = alltrue([for k, i in aws_api_gateway_integration.equilibreur : i.integration_http_method == k])
    error_message = "La méthode est transmise telle quelle (routage lectures / écritures de l'équilibreur)."
  }

  assert {
    condition     = aws_vpc_security_group_ingress_rule.equilibreur_depuis_lien.security_group_id == "sg-000000000000000a1" && aws_vpc_security_group_ingress_rule.equilibreur_depuis_lien.referenced_security_group_id == aws_security_group.lien_vpc.id
    error_message = "L'équilibreur n'accepte que le VPC Link."
  }
}

run "cache_desactive_journaux_chiffres" {
  command = apply

  assert {
    condition     = !aws_api_gateway_stage.api.cache_cluster_enabled && !aws_api_gateway_method_settings.toutes.settings[0].caching_enabled
    error_message = "Cache d'API Gateway désactivé (ADR 0031)."
  }

  assert {
    condition     = aws_api_gateway_stage.api.access_log_settings[0].destination_arn == aws_cloudwatch_log_group.acces.arn
    error_message = "Journaux d'accès dans le groupe dédié."
  }

  assert {
    condition     = alltrue([for g in [aws_cloudwatch_log_group.acces, aws_cloudwatch_log_group.execution, aws_cloudwatch_log_group.autorizer] : g.kms_key_id == var.cle_kms_journaux_arn && g.retention_in_days >= 365])
    error_message = "Journaux chiffrés par la CMK « logs », conservés un an au moins."
  }

  assert {
    condition     = aws_cloudwatch_log_group.execution.name == "API-Gateway-Execution-Logs_abc123defg/api"
    error_message = "Le groupe des journaux d'exécution porte le nom imposé par API Gateway."
  }

  assert {
    condition     = jsondecode(aws_api_gateway_stage.api.access_log_settings[0].format).clientId == "$context.authorizer.clientId"
    error_message = "Le clientId (azp) de l'autorizer est journalisé."
  }
}

run "plans_usage_debit_distinct_lecture_ecriture" {
  command = apply

  assert {
    condition     = toset(keys(aws_api_gateway_usage_plan.categorie)) == toset(["standard", "lot"])
    error_message = "Un plan d'usage par catégorie de clients."
  }

  assert {
    condition = (
      { for t in one(aws_api_gateway_usage_plan.categorie["standard"].api_stages).throttle : t.path => t.rate_limit }
      == { "/{proxy+}/GET" = 50, "/{proxy+}/HEAD" = 50, "/{proxy+}/PUT" = 10, "/{proxy+}/POST" = 10 }
    )
    error_message = "Débit par méthode : lectures et écritures distinctes."
  }

  assert {
    condition     = one(aws_api_gateway_usage_plan.categorie["lot"].quota_settings).limit == 500000 && length(aws_api_gateway_usage_plan.categorie["standard"].quota_settings) == 0
    error_message = "Quota quotidien seulement s'il est déclaré."
  }

  assert {
    condition     = aws_api_gateway_usage_plan_key.client["ledger-import"].usage_plan_id == aws_api_gateway_usage_plan.categorie["lot"].id
    error_message = "Chaque client est rattaché au plan de sa catégorie."
  }

  assert {
    condition = alltrue([for c, p in aws_ssm_parameter.cle_plan_usage : (
      p.name == "/ledger-dev/apigateway/cles-plan-usage/${c}" && p.type == "SecureString" && p.key_id == var.cle_kms_ssm_arn
      && p.value == aws_api_gateway_api_key.client[c].value
    )])
    error_message = "La clé de chaque client est recopiée dans SSM (SecureString, CMK « ssm »)."
  }
}

run "cache_de_l_autorizer_borne_par_la_duree_des_jetons" {
  command = plan

  variables {
    duree_cache_autorisation_secondes = 600
  }

  expect_failures = [var.duree_cache_autorisation_secondes]
}

run "un_client_dans_un_seul_plan" {
  command = plan

  variables {
    plans_usage = {
      a = { clients = ["ledger-exemple-lecture"], lecture = { debit = 1, rafale = 1 }, ecriture = { debit = 1, rafale = 1 } }
      b = { clients = ["ledger-exemple-lecture"], lecture = { debit = 1, rafale = 1 }, ecriture = { debit = 1, rafale = 1 } }
    }
  }

  expect_failures = [var.plans_usage]
}

run "version_exacte_du_paquet" {
  command = plan

  variables {
    paquet = { bucket = "ledger-dev-artefacts-111122223333-ca-central-1", version = "latest" }
  }

  expect_failures = [var.paquet]
}
