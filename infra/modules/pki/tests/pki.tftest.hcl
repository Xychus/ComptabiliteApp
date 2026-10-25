# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {
  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/ledger/ledger-dev-pki-lambda"
    }
  }

  mock_resource "aws_kms_key" {
    defaults = {
      arn = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-9999-4a5b-9c3d-0123456789ab"
    }
  }

  mock_resource "aws_cloudwatch_log_group" {
    defaults = {
      arn = "arn:aws:logs:ca-central-1:111122223333:log-group:/aws/lambda/ledger-dev-pki"
    }
  }

  mock_resource "aws_lambda_function" {
    defaults = {
      arn = "arn:aws:lambda:ca-central-1:111122223333:function:ledger-dev-pki"
    }
  }

  mock_resource "aws_secretsmanager_secret" {
    defaults = {
      arn = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:ledger-dev/pki/essai-AbCdEf"
    }
  }

  mock_resource "aws_cloudwatch_event_rule" {
    defaults = {
      arn = "arn:aws:events:ca-central-1:111122223333:rule/ledger-dev-pki-inventaire"
    }
  }
}

variables {
  prefixe                               = "ledger-dev"
  region                                = "ca-central-1"
  compte_aws                            = "111122223333"
  vpc_id                                = "vpc-0123456789abcdef0"
  sous_reseaux                          = ["subnet-0000000000000000a", "subnet-0000000000000000b"]
  groupe_securite_points_de_terminaison = "sg-0000000000000bbbb"
  depot_ecr_url                         = "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/pki-lambda"
  nom_grappe                            = "ledger-dev"
  administrateurs_kms                   = ["arn:aws:iam::111122223333:role/bootstrap/gha-apply"]
  cle_kms_secrets_arn                   = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-4444-4a5b-9c3d-0123456789ab"
  cle_kms_journaux_arn                  = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-1111-4a5b-9c3d-0123456789ab"
  cle_kms_lambda_arn                    = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-7777-4a5b-9c3d-0123456789ab"
  frontiere_permissions_arn             = "arn:aws:iam::111122223333:policy/bootstrap/ledger-frontiere-charges"

  identites = {
    debezium = { nom_commun = "debezium", usage = "client", services = ["debezium"] }
    kafka-1  = { nom_commun = "kafka-1", usage = "serveur-client", noms_dns = ["kafka-1.ledger-dev.interne"] }
    alb      = { nom_commun = "api", usage = "serveur", noms_dns = ["api.ledger-dev.interne"], certificat_acm_arn = "arn:aws:acm:ca-central-1:111122223333:certificate/abc" }
  }
}

run "sans_image_ni_ceremonie_secrets_seulement" {
  command = apply

  assert {
    condition     = length(aws_lambda_function.pki) == 0 && length(aws_secretsmanager_secret_rotation.certificat) == 0
    error_message = "Sans image publiée, ni Lambda ni rotation."
  }

  assert {
    condition     = length(aws_secretsmanager_secret.certificat) == 3 && alltrue([for s in aws_secretsmanager_secret.certificat : s.kms_key_id == var.cle_kms_secrets_arn])
    error_message = "Un secret par identité, chiffré par la CMK « secrets »."
  }

  assert {
    condition     = aws_secretsmanager_secret.certificat["kafka-1"].tags["pki:san"] == "kafka-1.ledger-dev.interne" && aws_secretsmanager_secret.certificat["kafka-1"].tags["pki:usage"] == "serveur-client"
    error_message = "Le profil est porté par les étiquettes du secret."
  }

  assert {
    condition     = aws_secretsmanager_secret.certificat["debezium"].tags["pki:grappe"] == "ledger-dev" && aws_secretsmanager_secret.certificat["debezium"].tags["pki:services"] == "debezium" && !contains(keys(aws_secretsmanager_secret.certificat["debezium"].tags), "pki:san")
    error_message = "Les services à redéployer sont étiquetés ; pas de SAN pour un client."
  }

  assert {
    condition     = aws_secretsmanager_secret.certificat["alb"].tags["pki:acm"] == "arn:aws:acm:ca-central-1:111122223333:certificate/abc"
    error_message = "Le certificat ACM à réimporter est étiqueté."
  }
}

run "mode_ceremonie" {
  command = apply

  variables {
    version_image             = "0.1.0"
    generations_intermediaire = ["2026"]
  }

  assert {
    condition     = length(aws_lambda_function.pki) == 1 && length(aws_secretsmanager_secret_rotation.certificat) == 0
    error_message = "Mode cérémonie : la Lambda existe (CSR) mais aucune rotation n'est activée."
  }

  assert {
    condition     = !contains(keys(aws_lambda_function.pki[0].environment[0].variables), "Ledger__Pki__CertificatIntermediaire")
    error_message = "Aucune intermédiaire n'est configurée en mode cérémonie."
  }

  assert {
    condition     = aws_kms_key.intermediaire["2026"].customer_master_key_spec == "ECC_NIST_P384" && aws_kms_key.intermediaire["2026"].key_usage == "SIGN_VERIFY"
    error_message = "La clé de l'intermédiaire est une clé KMS ECC P-384 de signature."
  }

  assert {
    condition     = aws_lambda_function.pki[0].image_uri == "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/pki-lambda:0.1.0" && aws_lambda_function.pki[0].kms_key_arn == var.cle_kms_lambda_arn
    error_message = "Image par version SemVer exacte, variables chiffrées par la CMK « lambda »."
  }

  assert {
    condition     = length(aws_cloudwatch_event_rule.inventaire) == 0
    error_message = "Pas d'inventaire avant l'installation de l'intermédiaire."
  }
}

run "seule_la_lambda_signe_des_condenses_sha384" {
  command = apply

  variables {
    version_image             = "0.1.0"
    generations_intermediaire = ["2026"]
  }

  assert {
    condition     = one([for s in jsondecode(aws_kms_key.intermediaire["2026"].policy).Statement : s if s.Sid == "SignatureParLaLambda"]).Principal.AWS == aws_iam_role.lambda.arn
    error_message = "Seul le rôle de la Lambda peut signer."
  }

  assert {
    condition     = one([for s in jsondecode(aws_kms_key.intermediaire["2026"].policy).Statement : s if s.Sid == "SignatureParLaLambda"]).Condition.StringEquals["kms:MessageType"] == "DIGEST"
    error_message = "Seuls des condensés sont signés."
  }

  assert {
    condition     = alltrue([for s in jsondecode(aws_kms_key.intermediaire["2026"].policy).Statement : !contains(flatten([s.Action]), "kms:Sign") if s.Sid != "SignatureParLaLambda"])
    error_message = "Aucun administrateur ne peut signer avec l'intermédiaire."
  }

  assert {
    condition     = aws_iam_role.lambda.path == "/ledger/" && aws_iam_role.lambda.permissions_boundary == var.frontiere_permissions_arn
    error_message = "Rôle sous /ledger/ avec la frontière de permissions."
  }

  assert {
    condition     = toset(one([for s in jsondecode(aws_iam_role_policy.lambda.policy).Statement : s.Resource if s.Sid == "RedeploiementServices"])) == toset(["arn:aws:ecs:ca-central-1:111122223333:service/ledger-dev/debezium"])
    error_message = "Seuls les services étiquetés peuvent être redéployés."
  }

  assert {
    condition     = one([for s in jsondecode(aws_iam_role_policy.lambda.policy).Statement : s.Resource if s.Sid == "ReimportAcm"]) == ["arn:aws:acm:ca-central-1:111122223333:certificate/abc"]
    error_message = "Seul le certificat ACM désigné peut être réimporté."
  }
}

run "intermediaire_installee_rotation_active" {
  command = apply

  variables {
    version_image                = "0.1.0"
    generations_intermediaire    = ["2026", "2027"]
    generation_active            = "2027"
    certificat_racine_pem        = "-----BEGIN CERTIFICATE-----\nracine\n-----END CERTIFICATE-----\n"
    certificat_intermediaire_pem = "-----BEGIN CERTIFICATE-----\nintermediaire\n-----END CERTIFICATE-----\n"
  }

  assert {
    condition     = length(aws_secretsmanager_secret_rotation.certificat) == 3 && alltrue([for r in aws_secretsmanager_secret_rotation.certificat : r.rotation_rules[0].automatically_after_days == 5])
    error_message = "Rotation tous les 5 jours de chaque certificat final."
  }

  assert {
    condition     = aws_lambda_function.pki[0].environment[0].variables["Ledger__Pki__CertificatIntermediaire"] == var.certificat_intermediaire_pem
    error_message = "L'intermédiaire active est fournie à la Lambda."
  }

  assert {
    condition     = length(aws_cloudwatch_event_rule.inventaire) == 1 && length(aws_cloudwatch_metric_alarm.echeance_certificats) == 1 && aws_cloudwatch_metric_alarm.echeance_certificats[0].threshold == 3
    error_message = "Inventaire quotidien et alarme d'échéance des certificats."
  }
}

run "mot_de_passe_de_debezium_renouvele_meme_en_mode_ceremonie" {
  command = apply

  variables {
    version_image             = "0.1.0"
    generations_intermediaire = ["2026"]
    mots_de_passe_postgres    = { debezium = { role = "debezium", services = ["debezium"] } }
    aurora = {
      hote              = "ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com"
      base              = "ledger"
      secret_maitre_arn = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:rds-cluster-abc"
      groupe_securite   = "sg-0000000000000aaaa"
    }
  }

  assert {
    condition     = length(aws_secretsmanager_secret_rotation.mot_de_passe) == 1 && length(aws_secretsmanager_secret_rotation.certificat) == 0
    error_message = "Le mot de passe tourne sans attendre l'intermédiaire ; les certificats attendent."
  }

  assert {
    condition = (aws_secretsmanager_secret.mot_de_passe["debezium"].tags["rotation:type"] == "mot-de-passe-postgres"
      && aws_secretsmanager_secret.mot_de_passe["debezium"].tags["pg:role"] == "debezium"
    && aws_secretsmanager_secret.mot_de_passe["debezium"].kms_key_id == var.cle_kms_secrets_arn)
    error_message = "Secret étiqueté par type et rôle, chiffré par la CMK « secrets »."
  }

  assert {
    condition     = aws_lambda_function.pki[0].environment[0].variables["Ledger__Pki__Postgres__SecretMaitre"] == var.aurora.secret_maitre_arn
    error_message = "La Lambda connaît Aurora et son secret maître."
  }

  assert {
    condition     = aws_vpc_security_group_ingress_rule.aurora_depuis_lambda[0].from_port == 5432 && aws_vpc_security_group_ingress_rule.aurora_depuis_lambda[0].security_group_id == "sg-0000000000000aaaa"
    error_message = "Aurora accepte la Lambda sur 5432."
  }
}

run "configuration_partielle_refusee" {
  command = plan

  variables {
    generations_intermediaire = ["2026"]
    generation_active         = "2026"
  }

  expect_failures = [var.certificat_intermediaire_pem]
}

run "generation_active_inconnue_refusee" {
  command = plan

  variables {
    generations_intermediaire = ["2026"]
    generation_active         = "2025"
  }

  expect_failures = [var.generation_active]
}

run "serveur_sans_nom_dns_refuse" {
  command = plan

  variables {
    identites = { query = { nom_commun = "query-api", usage = "serveur" } }
  }

  expect_failures = [var.identites]
}

run "rotation_trop_espacee_refusee" {
  command = plan

  variables {
    jours_entre_rotations = 7
  }

  expect_failures = [var.jours_entre_rotations]
}

run "latest_refuse" {
  command = plan

  variables {
    version_image = "latest"
  }

  expect_failures = [var.version_image]
}
