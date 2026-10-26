output "groupe_securite" {
  description = "Groupe de sécurité du service."
  value       = aws_security_group.service.id
}

output "role_tache_arn" {
  description = "ARN du rôle de tâche."
  value       = aws_iam_role.tache.arn
}

output "nom_service" {
  description = "Nom du service ECS (null tant qu'aucune image n'est publiée)."
  value       = one(aws_ecs_service.service[*].name)
}

output "groupe_journaux" {
  description = "Groupe de journaux du service."
  value       = aws_cloudwatch_log_group.service.name
}
