variable "prefixe" {
  description = "Préfixe des noms de ressources (ex. « ledger-dev »)."
  type        = string
}

variable "region" {
  description = "Région AWS (journaux awslogs, conditions des rôles)."
  type        = string
}

variable "compte_aws" {
  description = "Numéro du compte AWS (conditions de confiance du rôle d'exécution)."
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
  description = "Sous-réseaux « applications » où la tâche est lancée (repris en sortie pour run-task)."
  type        = list(string)
}

variable "depot_ecr_url" {
  description = "URL du dépôt ECR « migrations »."
  type        = string
}

variable "depot_ecr_arn" {
  description = "ARN du dépôt ECR « migrations » (droits de tirage du rôle d'exécution)."
  type        = string
}

variable "version_image" {
  description = "Étiquette de l'image : version SemVer exacte, jamais « latest »."
  type        = string

  validation {
    condition     = can(regex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$", var.version_image))
    error_message = "Une version SemVer exacte est exigée (ex. 0.3.1) ; « latest » est refusé."
  }
}

variable "hote_base" {
  description = "Point de terminaison rédacteur d'Aurora."
  type        = string
}

variable "nom_base" {
  description = "Nom de la base du journal."
  type        = string
}

variable "groupe_securite_aurora" {
  description = "Groupe de sécurité d'Aurora : le module y ajoute l'entrée 5432 depuis la tâche."
  type        = string
}

variable "groupe_securite_points_de_terminaison" {
  description = "Groupe des points de terminaison d'interface (ECR, Secrets Manager, CloudWatch Logs)."
  type        = string
}

variable "secret_maitre_arn" {
  description = "ARN du secret maître géré par RDS (JSON : username, password)."
  type        = string
}

variable "cle_kms_secrets_arn" {
  description = "ARN de la CMK « secrets » (déchiffrement du secret maître). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_secrets_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "cle_kms_journaux_arn" {
  description = "ARN de la CMK « logs » (journaux de la tâche). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_journaux_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "retention_journaux_jours" {
  description = "Durée de conservation des journaux de la tâche, en jours."
  type        = number
  default     = 365

  validation {
    condition     = var.retention_journaux_jours >= 365
    error_message = "Les journaux des migrations sont conservés au moins un an."
  }
}

variable "frontiere_permissions_arn" {
  description = "ARN de la frontière de permissions imposée aux rôles créés par le pipeline (amorçage)."
  type        = string
}
