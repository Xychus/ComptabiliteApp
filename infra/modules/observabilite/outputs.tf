output "groupe_journaux_metriques" {
  description = "Groupe de journaux des métriques EMF (collecteurs ADOT)."
  value       = aws_cloudwatch_log_group.metriques.name
}

output "tableau_de_bord" {
  description = "Nom du tableau de bord d'exploitation."
  value       = aws_cloudwatch_dashboard.exploitation.dashboard_name
}
