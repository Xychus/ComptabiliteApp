# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {
  mock_resource "aws_kms_key" {
    defaults = {
      arn = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-3333-4a5b-9c3d-0123456789ab"
    }
  }

  mock_resource "aws_iam_openid_connect_provider" {
    defaults = {
      arn = "arn:aws:iam::111122223333:oidc-provider/token.actions.githubusercontent.com"
    }
  }
}

variables {
  compte_aws          = "111122223333"
  environnement       = "dev"
  organisation_github = "Xychus"
  depot_github        = "ComptabiliteApp"
  administrateurs     = ["arn:aws:iam::111122223333:role/aws-reserved/sso.amazonaws.com/*/AWSReservedSSO_AdministratorAccess_*"]
}

run "etat_chiffre_versionne_et_prive" {
  command = apply

  assert {
    condition     = aws_s3_bucket.etat.bucket == "ledger-tfstate-111122223333-ca-central-1"
    error_message = "Le nom du bucket d'état suit la convention reconstruite par le pipeline."
  }

  assert {
    condition     = one(aws_s3_bucket_versioning.etat.versioning_configuration).status == "Enabled"
    error_message = "Le bucket d'état doit être versionné."
  }

  assert {
    condition = alltrue([
      for r in aws_s3_bucket_server_side_encryption_configuration.etat.rule :
      one(r.apply_server_side_encryption_by_default).sse_algorithm == "aws:kms"
      && one(r.apply_server_side_encryption_by_default).kms_master_key_id == aws_kms_key.etat.arn
    ])
    error_message = "L'état doit être chiffré par la CMK de l'état (jamais aws/s3)."
  }

  assert {
    condition = alltrue([
      aws_s3_bucket_public_access_block.etat.block_public_acls,
      aws_s3_bucket_public_access_block.etat.block_public_policy,
      aws_s3_bucket_public_access_block.etat.ignore_public_acls,
      aws_s3_bucket_public_access_block.etat.restrict_public_buckets,
    ])
    error_message = "Tout accès public au bucket d'état doit être bloqué."
  }

  assert {
    condition     = aws_kms_key.etat.enable_key_rotation && aws_kms_alias.etat.name == "alias/ledger-tfstate"
    error_message = "La clé de l'état doit tourner et porter l'alias alias/ledger-tfstate."
  }

  assert {
    condition     = contains([for s in jsondecode(aws_s3_bucket_policy.etat.policy).Statement : s.Sid if s.Effect == "Deny"], "TlsObligatoire")
    error_message = "Le bucket d'état doit refuser toute requête sans TLS."
  }
}

run "roles_du_pipeline_cloisonnes" {
  command = apply

  assert {
    condition     = jsondecode(aws_iam_role.plan.assume_role_policy).Statement[0].Condition.StringEquals["token.actions.githubusercontent.com:sub"] == "repo:Xychus/ComptabiliteApp:pull_request"
    error_message = "Le rôle de plan ne doit être assumable que depuis les pull requests du dépôt."
  }

  assert {
    condition     = jsondecode(aws_iam_role.apply.assume_role_policy).Statement[0].Condition.StringEquals["token.actions.githubusercontent.com:sub"] == "repo:Xychus/ComptabiliteApp:environment:dev"
    error_message = "Le rôle d'apply ne doit être assumable que depuis l'environnement GitHub protégé."
  }

  assert {
    condition = alltrue([
      for r in [aws_iam_role.plan, aws_iam_role.apply] :
      jsondecode(r.assume_role_policy).Statement[0].Condition.StringEquals["token.actions.githubusercontent.com:aud"] == "sts.amazonaws.com"
    ])
    error_message = "Les deux rôles exigent l'audience sts.amazonaws.com."
  }

  assert {
    condition     = aws_iam_role_policy_attachment.plan_lecture_seule.policy_arn == "arn:aws:iam::aws:policy/ReadOnlyAccess"
    error_message = "Le rôle de plan est en lecture seule."
  }

  assert {
    condition     = [for s in jsondecode(aws_iam_role_policy.plan_etat.policy).Statement : s.Resource if s.Sid == "Verrous"][0] == "arn:aws:s3:::ledger-tfstate-111122223333-ca-central-1/*.tflock"
    error_message = "Le rôle de plan n'écrit que les fichiers de verrou."
  }

  assert {
    condition     = aws_iam_role_policy_attachment.apply_services.policy_arn != "arn:aws:iam::aws:policy/AdministratorAccess"
    error_message = "Le rôle d'apply ne doit pas être administrateur."
  }
}

run "iam_du_pipeline_sous_frontiere" {
  command = apply

  assert {
    condition = alltrue([
      for s in jsondecode(aws_iam_role_policy.apply_iam.policy).Statement :
      s.Condition.StringEquals["iam:PermissionsBoundary"] == "arn:aws:iam::111122223333:policy/bootstrap/ledger-frontiere-charges"
      if s.Effect == "Allow" && contains(s.Action, "iam:CreateRole")
    ]) && length([for s in jsondecode(aws_iam_role_policy.apply_iam.policy).Statement : s if contains(s.Action, "iam:CreateRole")]) == 1
    error_message = "La création de rôle exige la frontière de permissions."
  }

  assert {
    condition = alltrue(flatten([
      for s in jsondecode(aws_iam_role_policy.apply_iam.policy).Statement : [
        for r in flatten([s.Resource]) : r == "*" || startswith(r, "arn:aws:iam::111122223333:role/ledger/") || startswith(r, "arn:aws:iam::111122223333:policy/ledger/")
      ] if s.Effect == "Allow"
    ]))
    error_message = "Le pipeline ne gère que les rôles et politiques du chemin /ledger/."
  }

  assert {
    condition     = contains([for s in jsondecode(aws_iam_policy.frontiere.policy).Statement : s.Sid if s.Effect == "Deny"], "AucuneGestionDeCompte")
    error_message = "La frontière doit exclure IAM, Organizations et Account."
  }

  assert {
    condition     = toset([for s in jsondecode(aws_iam_role_policy.apply_iam.policy).Statement : s.Sid if s.Effect == "Deny"]) == toset(["FrontiereInamovible", "EtatProtege", "CleEtatProtegee"])
    error_message = "L'état, sa clé et la frontière doivent être hors d'atteinte du pipeline."
  }
}

run "sujets_oidc_immuables" {
  command = apply

  variables {
    id_organisation_github = 153566096
    id_depot_github        = 1422798619
  }

  assert {
    condition     = jsondecode(aws_iam_role.plan.assume_role_policy).Statement[0].Condition.StringEquals["token.actions.githubusercontent.com:sub"] == "repo:Xychus@153566096/ComptabiliteApp@1422798619:pull_request"
    error_message = "Le rôle de plan doit accepter le sujet immuable des pull requests."
  }

  assert {
    condition     = jsondecode(aws_iam_role.apply.assume_role_policy).Statement[0].Condition.StringEquals["token.actions.githubusercontent.com:sub"] == "repo:Xychus@153566096/ComptabiliteApp@1422798619:environment:dev"
    error_message = "Le rôle d'apply doit accepter le sujet immuable de l'environnement protégé."
  }
}

run "refuse_un_identifiant_github_seul" {
  command = plan

  variables {
    id_organisation_github = 153566096
  }

  expect_failures = [var.id_depot_github]
}

run "refuse_un_environnement_inconnu" {
  command = plan

  variables {
    environnement = "staging"
  }

  expect_failures = [var.environnement]
}
