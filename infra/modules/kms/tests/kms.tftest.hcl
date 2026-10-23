# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {}

variables {
  prefixe         = "ledger"
  compte_aws      = "111122223333"
  region          = "ca-central-1"
  administrateurs = ["arn:aws:iam::111122223333:role/bootstrap/gha-apply"]
}

run "une_cle_par_domaine_avec_rotation" {
  command = apply

  assert {
    condition     = toset(keys(aws_kms_key.cle)) == toset(["aurora", "msk", "s3-archive", "secrets", "logs", "ecr", "ecs", "lambda", "ssm"])
    error_message = "Une clé par domaine de données est attendue."
  }

  assert {
    condition     = alltrue([for c in aws_kms_key.cle : c.enable_key_rotation && c.rotation_period_in_days == 365 && c.deletion_window_in_days == 30])
    error_message = "Chaque clé doit avoir la rotation activée et un délai de suppression de 30 jours."
  }

  assert {
    condition     = alltrue([for d, a in aws_kms_alias.alias : a.name == "alias/ledger-${d}"])
    error_message = "Chaque clé doit avoir l'alias alias/<préfixe>-<domaine>."
  }
}

run "aucun_usage_cryptographique_hors_service" {
  command = apply

  # Toute déclaration qui permet de déchiffrer ou de créer une autorisation passe par un service
  # AWS : kms:ViaService, ou principal de service avec une condition sur le contexte.
  assert {
    condition = alltrue(flatten([
      for c in aws_kms_key.cle : [
        for s in jsondecode(c.policy).Statement :
        length(setintersection(toset(flatten([s.Action])), toset(["kms:Decrypt", "kms:Encrypt", "kms:GenerateDataKey*", "kms:ReEncrypt*", "kms:CreateGrant", "kms:GenerateDataKeyWithoutPlaintext"]))) == 0
        || can(s.Condition.StringEquals["kms:ViaService"])
        || (can(s.Principal.Service) && can(s.Condition))
      ]
    ]))
    error_message = "Une déclaration accorde un usage cryptographique sans passer par un service AWS."
  }

  assert {
    condition = alltrue(flatten([
      for c in aws_kms_key.cle : [
        for s in jsondecode(c.policy).Statement :
        !contains(flatten([s.Action]), "kms:*") && try(s.Principal.AWS, "") != "*" && try(s.Principal, "") != "*"
      ]
    ]))
    error_message = "Aucune déclaration ne doit utiliser kms:* ni un principal générique."
  }

  assert {
    condition     = [for s in jsondecode(aws_kms_key.cle["aurora"].policy).Statement : s.Condition.StringEquals["kms:ViaService"] if s.Sid == "UsageViaService"][0] == ["rds.ca-central-1.amazonaws.com"]
    error_message = "La clé aurora n'est utilisable que via RDS."
  }

  assert {
    condition     = [for s in jsondecode(aws_kms_key.cle["logs"].policy).Statement : s.Principal.Service if s.Sid == "CloudWatchLogs"][0] == "logs.ca-central-1.amazonaws.com"
    error_message = "La clé logs doit être utilisable par CloudWatch Logs."
  }

  assert {
    condition     = length([for s in jsondecode(aws_kms_key.cle["ecs"].policy).Statement : s if try(s.Principal.Service, "") == "fargate.amazonaws.com"]) == 2
    error_message = "La clé ecs doit être utilisable par Fargate (stockage éphémère)."
  }
}

run "administration_reservee_aux_principaux_nommes" {
  command = apply

  assert {
    condition = alltrue([
      for c in aws_kms_key.cle :
      toset([for s in jsondecode(c.policy).Statement : s.Condition.ArnLike["aws:PrincipalArn"] if s.Sid == "Administration"][0])
      == toset(["arn:aws:iam::111122223333:role/bootstrap/gha-apply", "arn:aws:iam::111122223333:root"])
    ])
    error_message = "L'administration doit être réservée aux administrateurs déclarés et à l'utilisateur racine."
  }

  assert {
    condition = alltrue([
      for c in aws_kms_key.cle :
      length(setintersection(
        toset([for s in jsondecode(c.policy).Statement : s.Action if s.Sid == "Administration"][0]),
        toset(["kms:Decrypt", "kms:Encrypt", "kms:CreateGrant", "kms:ReEncrypt*", "kms:GenerateDataKey*"])
      )) == 0
    ])
    error_message = "Les administrateurs ne doivent obtenir aucune action cryptographique."
  }
}

run "refuse_une_liste_d_administrateurs_vide" {
  command = plan

  variables {
    administrateurs = []
  }

  expect_failures = [var.administrateurs]
}
