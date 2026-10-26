variable "prefixe" {
  description = "Préfixe des noms de ressources (ex. « ledger-dev »)."
  type        = string
}

variable "region" {
  description = "Région AWS."
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

variable "sous_reseaux" {
  description = "Sous-réseaux « applications » du service."
  type        = list(string)
}

variable "arn_grappe" {
  description = "ARN de la grappe ECS."
  type        = string
}

variable "groupe_securite_points_de_terminaison" {
  description = "Groupe des points de terminaison d'interface."
  type        = string
}

variable "depot_debezium_url" {
  description = "Dépôt ECR de la copie de l'image officielle quay.io/debezium/connect (ADR 0028)."
  type        = string
}

variable "depot_debezium_arn" {
  description = "ARN du dépôt ECR de l'image Debezium."
  type        = string
}

variable "version_debezium" {
  description = "Étiquette exacte de l'image Debezium (même version mineure qu'en local : 3.7)."
  type        = string
  default     = "3.7.0.Final"

  validation {
    condition     = can(regex("^[0-9]+\\.[0-9]+\\.[0-9]+\\.Final$", var.version_debezium))
    error_message = "Une version publiée exacte est exigée (ex. 3.7.0.Final) ; « latest » et les préversions sont refusées."
  }
}

variable "depot_sentinelle_url" {
  description = "Dépôt ECR de la sentinelle (installation du certificat client)."
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
  description = "Serveurs d'amorçage Kafka (mTLS)."
  type        = string
}

variable "groupe_securite_kafka" {
  description = "Groupe de sécurité des nœuds Kafka : le module y ajoute l'entrée 9093 depuis Debezium."
  type        = string
}

variable "port_kafka" {
  description = "Port de l'écouteur MTLS."
  type        = number
  default     = 9093
}

variable "secret_certificat" {
  description = "ARN du secret du certificat client « debezium » (module pki)."
  type        = string
}

variable "secret_mot_de_passe" {
  description = "ARN du secret du mot de passe PostgreSQL de Debezium (module pki, renouvelé tous les 5 jours)."
  type        = string
}

variable "hote_aurora" {
  description = "Point de terminaison rédacteur d'Aurora (le slot logique n'existe que sur le rédacteur)."
  type        = string
}

variable "nom_base" {
  description = "Nom de la base du journal."
  type        = string
}

variable "groupe_securite_aurora" {
  description = "Groupe de sécurité d'Aurora : le module y ajoute l'entrée 5432 depuis Debezium."
  type        = string
}

variable "certificat_racine_rds_pem" {
  description = "Certificats racines RDS de la région (PEM public, infra/rds/) : sslrootcert de la connexion verify-full."
  type        = string

  validation {
    condition     = strcontains(var.certificat_racine_rds_pem, "BEGIN CERTIFICATE") && length(var.certificat_racine_rds_pem) < 16000
    error_message = "Le paquet régional de certificats RDS (PEM, moins de 16 Ko) est exigé."
  }
}

variable "cle_kms_secrets_arn" {
  description = "ARN de la CMK « secrets ». Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_secrets_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "cle_kms_journaux_arn" {
  description = "ARN de la CMK « logs ». Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_journaux_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "frontiere_permissions_arn" {
  description = "ARN de la frontière de permissions des rôles créés par le pipeline."
  type        = string
}

variable "retention_journaux_jours" {
  description = "Conservation des journaux de Kafka Connect (jours)."
  type        = number
  default     = 365
}
