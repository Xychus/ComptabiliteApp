output "identifiant_grappe" {
  description = "Identifiant de la grappe Aurora."
  value       = aws_rds_cluster.grappe.cluster_identifier
}

output "point_de_terminaison_redacteur" {
  description = "Nom DNS du point de terminaison rédacteur (suit le basculement)."
  value       = aws_rds_cluster.grappe.endpoint
}

output "point_de_terminaison_lecteur" {
  description = "Nom DNS du point de terminaison lecteur."
  value       = aws_rds_cluster.grappe.reader_endpoint
}

output "port" {
  description = "Port PostgreSQL."
  value       = aws_rds_cluster.grappe.port
}

output "nom_base" {
  description = "Nom de la base du journal."
  value       = aws_rds_cluster.grappe.database_name
}

output "groupe_securite" {
  description = "Groupe de sécurité d'Aurora : chaque client y ajoute sa règle d'entrée (5432)."
  value       = aws_security_group.aurora.id
}

output "secret_maitre_arn" {
  description = "ARN du secret du mot de passe maître (géré et renouvelé par RDS, CMK « secrets »)."
  value       = aws_rds_cluster.grappe.master_user_secret[0].secret_arn
}

output "identifiants_instances" {
  description = "Identifiants des instances de la grappe."
  value       = [for i in aws_rds_cluster_instance.instance : i.identifier]
}

output "identifiant_ressource" {
  description = "Identifiant de ressource de la grappe (cluster-…) : ARN rds-db:connect de l'authentification IAM (ADR 0029)."
  value       = aws_rds_cluster.grappe.cluster_resource_id
}
