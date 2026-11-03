output "nom_bucket" {
  description = "Nom du bucket des paquets de déploiement."
  value       = aws_s3_bucket.artefacts.id
}

output "arn_bucket" {
  description = "ARN du bucket des paquets de déploiement."
  value       = aws_s3_bucket.artefacts.arn
}
