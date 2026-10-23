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
