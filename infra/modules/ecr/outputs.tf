output "urls" {
  description = "URL de chaque dépôt, par service."
  value       = { for service, depot in aws_ecr_repository.depot : service => depot.repository_url }
}

output "arns" {
  description = "ARN de chaque dépôt, par service."
  value       = { for service, depot in aws_ecr_repository.depot : service => depot.arn }
}
