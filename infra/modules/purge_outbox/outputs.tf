output "groupe_securite" {
  description = "Groupe de sécurité de la tâche de purge."
  value       = aws_security_group.purge.id
}

output "famille" {
  description = "Famille de la définition de tâche (null tant qu'aucune image n'est publiée)."
  value       = one(aws_ecs_task_definition.purge[*].family)
}
