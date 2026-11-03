# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {
  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/ledger/ledger-dev-purge-outbox"
    }
  }

  mock_resource "aws_cloudwatch_log_group" {
    defaults = {
      arn = "arn:aws:logs:ca-central-1:111122223333:log-group:/ledger-dev/purge-outbox"
    }
  }

  mock_resource "aws_ecs_task_definition" {
    defaults = {
      arn = "arn:aws:ecs:ca-central-1:111122223333:task-definition/ledger-dev-purge-outbox:1"
    }
  }
}

variables {
  prefixe                               = "ledger-dev"
  region                                = "ca-central-1"
  compte_aws                            = "111122223333"
  vpc_id                                = "vpc-0123456789abcdef0"
  sous_reseaux                          = ["subnet-0000000000000001a"]
  arn_grappe                            = "arn:aws:ecs:ca-central-1:111122223333:cluster/ledger-dev"
  groupe_securite_points_de_terminaison = "sg-0000000000000bbbb"
  depot_ecr_url                         = "111122223333.dkr.ecr.ca-central-1.amazonaws.com/ledger/migrations"
  depot_ecr_arn                         = "arn:aws:ecr:ca-central-1:111122223333:repository/ledger/migrations"
  version_image                         = "0.2.0"
  hote_aurora                           = "ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com"
  nom_base                              = "ledger"
  identifiant_ressource_aurora          = "cluster-ABCDEFGHIJKL"
  groupe_securite_aurora                = "sg-0000000000000aaaa"
  cle_kms_journaux_arn                  = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-1111-4a5b-9c3d-0123456789ab"
  frontiere_permissions_arn             = "arn:aws:iam::111122223333:policy/bootstrap/ledger-frontiere-charges"
}

run "purge_planifiee_avec_le_role_de_maintenance_sans_mot_de_passe" {
  command = apply

  assert {
    condition     = jsondecode(aws_ecs_task_definition.purge[0].container_definitions)[0].command == ["purger-outbox"]
    error_message = "La tâche lance la commande de purge de l'image des migrations."
  }

  assert {
    condition     = one([for s in jsondecode(aws_iam_role_policy.tache.policy).Statement : s.Resource]) == "arn:aws:rds-db:ca-central-1:111122223333:dbuser:cluster-ABCDEFGHIJKL/ledger_maintenance"
    error_message = "Seule la connexion IAM de ledger_maintenance est permise."
  }

  assert {
    condition     = alltrue([for e in jsondecode(aws_ecs_task_definition.purge[0].container_definitions)[0].environment : !strcontains(lower(e.name), "mot_de_passe")])
    error_message = "Aucun mot de passe."
  }

  assert {
    condition     = aws_cloudwatch_event_target.purge[0].ecs_target[0].launch_type == "FARGATE" && aws_cloudwatch_event_rule.purge[0].schedule_expression == "cron(0 4 * * ? *)"
    error_message = "Purge quotidienne sur Fargate."
  }

  assert {
    condition     = one([for s in jsondecode(aws_iam_role_policy.planificateur.policy).Statement : s.Condition.StringEquals["iam:PassedToService"] if s.Sid == "PassageRoles"]) == "ecs-tasks.amazonaws.com"
    error_message = "Les rôles de la tâche ne sont passés qu'à ECS."
  }
}

run "retention_invalide_refusee" {
  command = plan

  variables {
    retention_heures = 0
  }

  expect_failures = [var.retention_heures]
}
