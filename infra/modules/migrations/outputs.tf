output "definition_tache_arn" {
  description = "ARN de la révision de la définition de tâche des migrations."
  value       = aws_ecs_task_definition.migrations.arn
}

output "famille" {
  description = "Famille de la définition de tâche (run-task --task-definition)."
  value       = aws_ecs_task_definition.migrations.family
}

output "configuration_reseau" {
  description = "Configuration réseau de run-task : sous-réseaux privés, groupe de sécurité, sans adresse publique."
  value = {
    sous_reseaux    = var.sous_reseaux
    groupe_securite = aws_security_group.migrations.id
  }
}
