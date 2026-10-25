# Grappe ECS (phase 7 : Fargate seulement, pour la tâche de migrations). La phase 8 y ajoute le
# groupe de capacité EC2 des brokers Kafka, Service Connect (découverte) et les services.
# Stockage éphémère des tâches Fargate chiffré par la CMK « ecs » (CLAUDE.md, « Sécurité »).

resource "aws_ecs_cluster" "grappe" {
  name = var.prefixe

  setting {
    name  = "containerInsights"
    value = "enabled"
  }

  configuration {
    managed_storage_configuration {
      fargate_ephemeral_storage_kms_key_id = var.cle_kms_ecs_arn
    }
  }
}

resource "aws_ecs_cluster_capacity_providers" "grappe" {
  cluster_name       = aws_ecs_cluster.grappe.name
  capacity_providers = ["FARGATE"]

  default_capacity_provider_strategy {
    capacity_provider = "FARGATE"
    weight            = 1
  }
}
