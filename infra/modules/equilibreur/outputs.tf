output "groupes_cibles" {
  description = "ARN du groupe cible de chaque API."
  value       = { for nom, g in aws_lb_target_group.cible : nom => g.arn }
}

output "suffixes_groupes_cibles" {
  description = "Suffixe d'ARN du groupe cible de chaque API (dimensions CloudWatch)."
  value       = { for nom, g in aws_lb_target_group.cible : nom => g.arn_suffix }
}

output "suffixe_equilibreur" {
  description = "Suffixe d'ARN de l'équilibreur (dimension CloudWatch LoadBalancer)."
  value       = aws_lb.api.arn_suffix
}

output "groupe_securite" {
  description = "Groupe de sécurité de l'équilibreur."
  value       = aws_security_group.equilibreur.id
}

output "nom_hote" {
  description = "Nom de l'API dans le certificat public : hôte de l'intégration d'API Gateway."
  value       = local.nom_hote
}

output "arn" {
  description = "ARN de l'équilibreur : cible de l'intégration d'API Gateway par le VPC Link (integration_target)."
  value       = aws_lb.api.arn
}
