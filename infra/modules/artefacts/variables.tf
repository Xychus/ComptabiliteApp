variable "prefixe" {
  description = "Préfixe du nom du bucket (ex. « ledger-dev »)."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,20}[a-z0-9]$", var.prefixe))
    error_message = "Le préfixe doit être en minuscules (lettres, chiffres, tirets), 22 caractères au plus."
  }
}

variable "compte_aws" {
  description = "Numéro du compte AWS (nom du bucket unique au monde)."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{12}$", var.compte_aws))
    error_message = "Le numéro de compte AWS compte 12 chiffres."
  }
}

variable "region" {
  description = "Région AWS du bucket."
  type        = string
}

variable "cle_kms_arn" {
  description = "ARN de la CMK « lambda ». Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "suppression_forcee" {
  description = "Vider le bucket à sa destruction (true : dev jetable, ADR 0027 ; les paquets se republient depuis Git)."
  type        = bool
  default     = false
}
