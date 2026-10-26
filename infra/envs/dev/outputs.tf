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

output "aurora" {
  description = "Grappe Aurora : points de terminaison, port, base, secret maître (ARN seulement)."
  value = {
    identifiant       = module.aurora.identifiant_grappe
    redacteur         = module.aurora.point_de_terminaison_redacteur
    lecteur           = module.aurora.point_de_terminaison_lecteur
    port              = module.aurora.port
    base              = module.aurora.nom_base
    secret_maitre_arn = module.aurora.secret_maitre_arn
  }
}

output "bucket_archive" {
  description = "Nom du bucket d'archive (Object Lock COMPLIANCE)."
  value       = module.archive.nom_bucket
}

output "grappe_ecs" {
  description = "Nom de la grappe ECS."
  value       = module.ecs.nom_grappe
}

output "migrations" {
  description = "Lancement de la tâche de migrations (run-task) : famille et configuration réseau."
  value = {
    grappe          = module.ecs.nom_grappe
    famille         = module.migrations.famille
    sous_reseaux    = module.migrations.configuration_reseau.sous_reseaux
    groupe_securite = module.migrations.configuration_reseau.groupe_securite
  }
}

output "execution" {
  description = "Exécution (phase 8) : Kafka, initialisation, équilibreur, Lambda PKI, tableau de bord."
  value = {
    serveurs_kafka       = module.kafka.serveurs
    initialisation_kafka = module.kafka.initialisation
    api                  = module.equilibreur.nom_hote
    fonction_pki         = module.pki.fonction_lambda
    rotation_certificats = module.pki.rotation_active
    cles_intermediaire   = module.pki.cles_intermediaire
    tableau_de_bord      = module.observabilite.tableau_de_bord
  }
}
