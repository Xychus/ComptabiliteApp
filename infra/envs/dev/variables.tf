variable "compte_aws" {
  description = "Numéro du compte AWS de l'environnement (fourni par le pipeline : TF_VAR_compte_aws)."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{12}$", var.compte_aws))
    error_message = "Le numéro de compte AWS compte 12 chiffres."
  }
}

variable "region" {
  description = "Région AWS de l'environnement."
  type        = string
  default     = "ca-central-1"
}

variable "zones" {
  description = "Les trois zones de disponibilité de l'environnement (liste figée : aucun ajout silencieux par AWS)."
  type        = list(string)
  default     = ["ca-central-1a", "ca-central-1b", "ca-central-1d"]
}

variable "cidr_vpc" {
  description = "Bloc CIDR du VPC de l'environnement."
  type        = string
  default     = "10.20.0.0/16"
}

variable "administrateurs_kms" {
  description = "Administrateurs des CMK en plus du rôle gha-apply et de l'utilisateur racine (motifs ArnLike)."
  type        = list(string)
  default     = []
}

variable "capacite_aurora" {
  description = "Capacité Aurora Serverless v2 (ACU). Le rédacteur ne se met jamais en pause (réplication logique) ; le lecteur, si (ADR 0027)."
  type = object({
    minimum_acu          = number
    maximum_acu          = number
    secondes_avant_pause = optional(number, 300)
  })
  default = {
    minimum_acu          = 0
    maximum_acu          = 4
    secondes_avant_pause = 900
  }
}

variable "version_image_migrations" {
  description = <<-EOT
    Étiquette SemVer exacte de l'image des migrations. Fournie par le déploiement (deploy.yml,
    phase 10) ; d'ici là, aucune image n'est publiée et la tâche n'est pas lancée.
  EOT
  type        = string
  default     = "0.1.0"
}
