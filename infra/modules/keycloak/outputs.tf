output "nom_hote" {
  description = "Nom DNS de Keycloak (SAN de son certificat serveur)."
  value       = local.nom_hote
}

output "url_realm" {
  description = "URL du realm « ledger » : émetteur des jetons et base de la découverte OIDC."
  value       = "https://${local.nom_hote}:${local.port_https}/realms/ledger"
}

output "groupe_securite" {
  description = "Groupe de sécurité des tâches Keycloak."
  value       = aws_security_group.keycloak.id
}

output "nom_service" {
  description = "Nom du service ECS (redéployé par la Lambda PKI après chaque renouvellement)."
  value       = "keycloak"
}

output "environnement" {
  description = "Variables d'environnement du conteneur Keycloak (vérifiées par les tests)."
  value       = local.environnement_keycloak
}
