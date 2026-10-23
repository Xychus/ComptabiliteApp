output "arns" {
  description = "ARN de la clé de chaque domaine (aurora, msk, s3-archive, secrets, logs, ecr, ecs, lambda, ssm)."
  value       = { for domaine, cle in aws_kms_key.cle : domaine => cle.arn }
}

output "alias" {
  description = "Nom d'alias de la clé de chaque domaine."
  value       = { for domaine, alias in aws_kms_alias.alias : domaine => alias.name }
}
