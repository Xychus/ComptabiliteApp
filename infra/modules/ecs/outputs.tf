output "nom_grappe" {
  description = "Nom de la grappe ECS."
  value       = aws_ecs_cluster.grappe.name
}

output "arn_grappe" {
  description = "ARN de la grappe ECS."
  value       = aws_ecs_cluster.grappe.arn
}
