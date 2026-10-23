output "bucket_etat" {
  description = "Bucket de l'état Terraform de ce compte."
  value       = aws_s3_bucket.etat.bucket
}

output "cle_etat_arn" {
  description = "ARN de la CMK de l'état."
  value       = aws_kms_key.etat.arn
}

output "alias_cle_etat_arn" {
  description = "ARN de l'alias de la CMK de l'état (valeur de kms_key_id des backends)."
  value       = local.arn_alias_etat
}

output "role_plan_arn" {
  description = "Rôle assumé par le pipeline pour terraform plan (pull requests)."
  value       = aws_iam_role.plan.arn
}

output "role_apply_arn" {
  description = "Rôle assumé par le pipeline pour terraform apply (environnement GitHub protégé)."
  value       = aws_iam_role.apply.arn
}

output "frontiere_permissions_arn" {
  description = "Frontière de permissions imposée aux rôles créés par le pipeline."
  value       = aws_iam_policy.frontiere.arn
}
