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
  description = "Sous-réseaux « applications » de la tâche."
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

variable "depot_ecr_url" {
  description = "Dépôt ECR de l'image des migrations (commande « purger-outbox »)."
  type        = string
}

variable "depot_ecr_arn" {
  description = "ARN du dépôt ECR des migrations."
  type        = string
}

variable "version_image" {
  description = "Version SemVer exacte de l'image des migrations ; null tant qu'aucune image n'est publiée."
  type        = string
  default     = null

  validation {
    condition     = var.version_image == null || can(regex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$", var.version_image))
    error_message = "Une version SemVer exacte est exigée ; « latest » est refusé."
  }
}

variable "hote_aurora" {
  description = "Point de terminaison rédacteur d'Aurora."
  type        = string
}

variable "nom_base" {
  description = "Nom de la base du journal."
  type        = string
}

variable "identifiant_ressource_aurora" {
  description = "Identifiant de ressource de la grappe (cluster-…), pour rds-db:connect."
  type        = string
}

variable "groupe_securite_aurora" {
  description = "Groupe de sécurité d'Aurora : le module y ajoute l'entrée 5432 depuis la tâche."
  type        = string
}

variable "retention_heures" {
  description = "Rétention des lignes de l'outbox (heures, au moins 1 ; ADR 0020)."
  type        = number
  default     = 168

  validation {
    condition     = var.retention_heures >= 1
    error_message = "La rétention est d'au moins une heure."
  }
}

variable "planification" {
  description = "Expression de planification EventBridge (UTC)."
  type        = string
  default     = "cron(0 4 * * ? *)"
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
  description = "Conservation des journaux de la purge (jours)."
  type        = number
  default     = 365
}
