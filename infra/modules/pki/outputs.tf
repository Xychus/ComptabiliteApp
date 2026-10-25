output "secrets_certificats" {
  description = "ARN du secret de chaque certificat final (clé = nom de l'identité) ; champs JSON : certificat, chaine, cle, racine, expire_le."
  value       = { for cle, s in aws_secretsmanager_secret.certificat : cle => s.arn }
}

output "cles_intermediaire" {
  description = "ARN de la clé KMS de chaque génération de l'intermédiaire."
  value       = { for g, k in aws_kms_key.intermediaire : g => k.arn }
}

output "fonction_lambda" {
  description = "Nom de la Lambda PKI (null tant qu'aucune image n'est publiée)."
  value       = one(aws_lambda_function.pki[*].function_name)
}

output "rotation_active" {
  description = "Vrai si l'intermédiaire est installée et la rotation des certificats finaux activée."
  value       = local.rotation_active
}

output "certificat_racine_pem" {
  description = "Certificat racine (public) : ancre de confiance des magasins."
  value       = var.certificat_racine_pem
}
