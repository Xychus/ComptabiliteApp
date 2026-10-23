output "vpc_id" {
  description = "Identifiant du VPC."
  value       = module.reseau.vpc_id
}

output "sous_reseaux_applications" {
  description = "Sous-réseaux « applications » (un par zone)."
  value       = module.reseau.sous_reseaux_applications
}

output "sous_reseaux_donnees" {
  description = "Sous-réseaux « donnees » (un par zone)."
  value       = module.reseau.sous_reseaux_donnees
}

output "cles_kms" {
  description = "ARN de la CMK de chaque domaine."
  value       = module.kms.arns
}

output "depots_ecr" {
  description = "URL du dépôt ECR de chaque service."
  value       = module.ecr.urls
}
