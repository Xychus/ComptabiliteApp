output "serveurs" {
  description = "Serveurs d'amorçage des clients (mTLS) : kafka-N.kafka.<domaine>:9093, séparés par des virgules."
  value       = local.serveurs
}

output "noms_noeuds" {
  description = "Nom DNS de chaque nœud (SAN de son certificat)."
  value       = { for nom, n in local.noeuds : nom => n.dns }
}

output "groupe_securite" {
  description = "Groupe de sécurité des nœuds : chaque client y ajoute son entrée 9093 (comme pour Aurora)."
  value       = aws_security_group.noeuds.id
}

output "port_client" {
  description = "Port de l'écouteur MTLS des clients."
  value       = local.port_client
}

output "initialisation" {
  description = "Tâche d'initialisation (sujet ledger.events et ACL) : famille et configuration réseau de run-task."
  value = {
    famille         = one(aws_ecs_task_definition.initialisation[*].family)
    sous_reseaux    = var.sous_reseaux_applications
    groupe_securite = aws_security_group.initialisation.id
  }
}
