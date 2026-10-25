# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {
  mock_resource "aws_cloudwatch_log_group" {
    defaults = {
      arn = "arn:aws:logs:ca-central-1:111122223333:log-group:/ledger-dev/migrations"
    }
  }

  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/ledger/ledger-dev-migrations-execution"
    }
  }
}

variables {
  prefixe                               = "ledger-dev"
  region                                = "ca-central-1"
  compte_aws                            = "111122223333"
  vpc_id                                = "vpc-0123456789abcdef0"
  sous_reseaux                          = ["subnet-0000000000000000a", "subnet-0000000000000000b", "subnet-0000000000000000c"]
  depot_ecr_url                         = "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/migrations"
  depot_ecr_arn                         = "arn:aws:ecr:ca-central-1:111122223333:repository/ledger/migrations"
  version_image                         = "0.1.0"
  hote_base                             = "ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com"
  nom_base                              = "ledger"
  groupe_securite_aurora                = "sg-0000000000000aaaa"
  groupe_securite_points_de_terminaison = "sg-0000000000000bbbb"
  secret_maitre_arn                     = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:rds!cluster-abc-123456"
  cle_kms_secrets_arn                   = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-4444-4a5b-9c3d-0123456789ab"
  cle_kms_journaux_arn                  = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-1111-4a5b-9c3d-0123456789ab"
  frontiere_permissions_arn             = "arn:aws:iam::111122223333:policy/bootstrap/ledger-frontiere-charges"
}

run "aucun_secret_en_clair" {
  command = apply

  assert {
    condition     = jsondecode(aws_ecs_task_definition.migrations.container_definitions)[0].image == "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/migrations:0.1.0"
    error_message = "L'image doit porter la version SemVer exacte."
  }

  assert {
    condition = (toset([for s in jsondecode(aws_ecs_task_definition.migrations.container_definitions)[0].secrets : s.valueFrom])
    == toset(["${var.secret_maitre_arn}:username::", "${var.secret_maitre_arn}:password::"]))
    error_message = "Utilisateur et mot de passe doivent venir du secret maître (secrets ECS)."
  }

  assert {
    condition     = alltrue([for e in jsondecode(aws_ecs_task_definition.migrations.container_definitions)[0].environment : !strcontains(lower(e.name), "mot_de_passe") && !strcontains(lower(e.name), "connection_string")])
    error_message = "Aucun mot de passe ni chaîne de connexion en variable d'environnement en clair."
  }

  assert {
    condition     = jsondecode(aws_ecs_task_definition.migrations.container_definitions)[0].readonlyRootFilesystem && jsondecode(aws_ecs_task_definition.migrations.container_definitions)[0].user == "1654"
    error_message = "Système de fichiers en lecture seule, utilisateur non root."
  }

  assert {
    condition     = aws_ecs_task_definition.migrations.task_role_arn == null || aws_ecs_task_definition.migrations.task_role_arn == ""
    error_message = "La tâche n'a besoin d'aucun rôle de tâche."
  }
}

run "role_d_execution_au_moindre_privilege" {
  command = apply

  assert {
    condition     = aws_iam_role.execution.path == "/ledger/" && aws_iam_role.execution.permissions_boundary == var.frontiere_permissions_arn
    error_message = "Le rôle d'exécution doit être sous /ledger/ avec la frontière de permissions."
  }

  assert {
    condition     = one([for s in jsondecode(aws_iam_role_policy.execution.policy).Statement : s.Resource if s.Sid == "SecretMaitre"]) == var.secret_maitre_arn
    error_message = "Seul le secret maître peut être lu."
  }

  assert {
    condition     = one([for s in jsondecode(aws_iam_role_policy.execution.policy).Statement : s.Condition.StringEquals["kms:ViaService"] if s.Sid == "DechiffrementSecret"]) == "secretsmanager.ca-central-1.amazonaws.com"
    error_message = "Le déchiffrement n'est permis qu'à travers Secrets Manager."
  }

  assert {
    condition     = aws_cloudwatch_log_group.migrations.kms_key_id == var.cle_kms_journaux_arn
    error_message = "Les journaux de la tâche doivent être chiffrés par la CMK « logs »."
  }
}

run "reseau_ferme" {
  command = apply

  assert {
    condition     = aws_vpc_security_group_egress_rule.vers_aurora.referenced_security_group_id == var.groupe_securite_aurora && aws_vpc_security_group_egress_rule.vers_aurora.from_port == 5432
    error_message = "Sortie PostgreSQL vers le seul groupe d'Aurora."
  }

  assert {
    condition     = aws_vpc_security_group_ingress_rule.aurora_depuis_migrations.security_group_id == var.groupe_securite_aurora && aws_vpc_security_group_ingress_rule.aurora_depuis_migrations.referenced_security_group_id == aws_security_group.migrations.id
    error_message = "Aurora doit accepter la tâche de migrations, et elle seule par cette règle."
  }
}

run "refuse_latest" {
  command = plan

  variables {
    version_image = "latest"
  }

  expect_failures = [var.version_image]
}
