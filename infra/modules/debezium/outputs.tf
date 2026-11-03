output "groupe_securite" {
  description = "Groupe de sécurité de Kafka Connect."
  value       = aws_security_group.debezium.id
}

output "configuration_connecteur" {
  description = "Configuration fusionnée du connecteur ledger-outbox (sans secret : le mot de passe est une référence ${"$"}{env:…})."
  value       = local.connecteur
}
