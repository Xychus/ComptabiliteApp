output "id_api" {
  description = "Identifiant de l'API privée (nom DNS privé : <id>.execute-api.<région>.amazonaws.com)."
  value       = aws_api_gateway_rest_api.api.id
}

output "url" {
  description = "URL de base de l'étape, joignable seulement depuis le VPC (DNS privé du point de terminaison)."
  value       = "https://${aws_api_gateway_rest_api.api.id}.execute-api.${var.region}.amazonaws.com/${aws_api_gateway_stage.api.stage_name}"
}

output "nom_autorizer" {
  description = "Nom de la Lambda autorizer."
  value       = aws_lambda_function.autorizer.function_name
}

output "prefixe_cles_plan_usage" {
  description = "Préfixe SSM des clés de plan d'usage (un paramètre par client)."
  value       = local.prefixe_cles
}
