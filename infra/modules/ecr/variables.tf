variable "prefixe" {
  description = "Espace de noms des dépôts (ex. « ledger » donne ledger/command-api)."
  type        = string
}

variable "depots" {
  description = "Un dépôt par service conteneurisé."
  type        = set(string)

  validation {
    condition     = length(var.depots) > 0 && alltrue([for d in var.depots : can(regex("^[a-z][a-z0-9-]*[a-z0-9]$", d))])
    error_message = "Noms de dépôts en minuscules (lettres, chiffres, tirets), au moins un."
  }
}

variable "cle_kms_arn" {
  description = "ARN de la CMK du domaine « ecr ». Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "jours_conservation_sans_etiquette" {
  description = "Délai avant l'expiration des images sans étiquette (couches orphelines d'une poussée interrompue)."
  type        = number
  default     = 7
}

variable "suppression_forcee" {
  description = "Supprimer un dépôt même s'il contient des images. true : environnement jetable seulement (dev)."
  type        = bool
  default     = false
}
