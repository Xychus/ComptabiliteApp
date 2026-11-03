variable "prefixe" {
  description = "Préfixe des noms de ressources (ex. « ledger-dev »)."
  type        = string
}

variable "region" {
  description = "Région AWS (journaux, conditions des politiques)."
  type        = string
}

variable "compte_aws" {
  description = "Numéro du compte AWS."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{12}$", var.compte_aws))
    error_message = "Le numéro de compte AWS compte 12 chiffres."
  }
}

variable "vpc_id" {
  description = "Identifiant du VPC."
  type        = string
}

variable "cidr_vpc" {
  description = "CIDR du VPC : clients de Keycloak (API, autorizer, applications internes)."
  type        = string
}

variable "sous_reseaux" {
  description = "Sous-réseaux « applications » des tâches."
  type        = list(string)
}

variable "arn_grappe" {
  description = "ARN de la grappe ECS."
  type        = string
}

variable "domaine_interne" {
  description = "Domaine de la zone privée (ex. « ledger-dev.internal ») : Keycloak est publié sous idp.<domaine>."
  type        = string
}

variable "groupe_securite_points_de_terminaison" {
  description = "Groupe des points de terminaison d'interface (ECR, Secrets Manager, CloudWatch)."
  type        = string
}

variable "depot_keycloak_url" {
  description = "URL du dépôt ECR de la copie de l'image officielle quay.io/keycloak/keycloak."
  type        = string
}

variable "depot_keycloak_arn" {
  description = "ARN du dépôt ECR de Keycloak."
  type        = string
}

variable "version_keycloak" {
  description = "Étiquette exacte de l'éditeur copiée dans ECR (ex. « 26.8.1 »)."
  type        = string

  validation {
    condition     = can(regex("^[0-9]+[.][0-9]+[.][0-9]+$", var.version_keycloak))
    error_message = "Une version exacte est exigée (ex. 26.8.1) ; « latest » et les préversions sont refusés."
  }
}

variable "depot_sentinelle_url" {
  description = "URL du dépôt ECR de la sentinelle PKI (installation du certificat serveur)."
  type        = string
}

variable "depot_sentinelle_arn" {
  description = "ARN du dépôt ECR de la sentinelle."
  type        = string
}

variable "version_sentinelle" {
  description = "Version SemVer exacte de la sentinelle ; null tant qu'aucune image n'est publiée (aucun service créé)."
  type        = string
  default     = null
}

variable "serveurs_kafka" {
  description = "Serveurs Kafka exigés par la configuration de la sentinelle (non contactés en mode « installer »)."
  type        = string
}

variable "secret_certificat" {
  description = "ARN du secret du certificat serveur « keycloak » de la PKI (renouvelé, service redéployé)."
  type        = string
}

variable "secret_mot_de_passe" {
  description = "ARN du secret du mot de passe du rôle PostgreSQL « keycloak » (renouvelé tous les 5 jours)."
  type        = string
}

variable "hote_aurora" {
  description = "Point de terminaison rédacteur d'Aurora."
  type        = string
}

variable "nom_base" {
  description = "Base du grand livre (schéma « keycloak », migration 0010)."
  type        = string
}

variable "groupe_securite_aurora" {
  description = "Groupe de sécurité d'Aurora (règle d'entrée ajoutée par ce module)."
  type        = string
}

variable "certificat_racine_rds_pem" {
  description = "Paquet régional des certificats racines RDS (infra/rds) : connexion verify-full."
  type        = string
}

variable "nombre_taches" {
  description = "Nombre de tâches Keycloak (grappe par jdbc-ping au-delà d'une)."
  type        = number
  default     = 1

  validation {
    condition     = var.nombre_taches >= 1
    error_message = "Au moins une tâche."
  }
}

variable "cle_kms_secrets_arn" {
  description = "CMK « secrets » (déchiffrement des secrets via Secrets Manager)."
  type        = string
}

variable "cle_kms_journaux_arn" {
  description = "CMK « logs » du groupe de journaux."
  type        = string
}

variable "frontiere_permissions_arn" {
  description = "Frontière de permissions des rôles créés (ADR 0021)."
  type        = string
}

variable "retention_journaux_jours" {
  description = "Conservation des journaux de Keycloak."
  type        = number
  default     = 365
}
