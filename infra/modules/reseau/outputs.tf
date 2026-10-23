output "vpc_id" {
  description = "Identifiant du VPC."
  value       = aws_vpc.vpc.id
}

output "cidr_vpc" {
  description = "Bloc CIDR du VPC."
  value       = aws_vpc.vpc.cidr_block
}

output "sous_reseaux_applications" {
  description = "Identifiants des sous-réseaux « applications », un par zone, dans l'ordre des zones."
  value       = [for i in range(length(var.zones)) : aws_subnet.sous_reseau["applications-${i}"].id]
}

output "sous_reseaux_donnees" {
  description = "Identifiants des sous-réseaux « donnees », un par zone, dans l'ordre des zones."
  value       = [for i in range(length(var.zones)) : aws_subnet.sous_reseau["donnees-${i}"].id]
}

output "groupe_securite_points_de_terminaison" {
  description = "Groupe de sécurité des points de terminaison d'interface (HTTPS depuis le VPC)."
  value       = aws_security_group.points_de_terminaison.id
}

output "tables_de_routage" {
  description = "Identifiant de la table de routage de chaque niveau."
  value       = { for niveau, t in aws_route_table.niveau : niveau => t.id }
}
