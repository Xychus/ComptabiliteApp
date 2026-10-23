variable "prefixe" {
  description = "Préfixe des alias de clés (ex. « ledger » donne alias/ledger-aurora)."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,30}[a-z0-9]$", var.prefixe))
    error_message = "Le préfixe doit être en minuscules (lettres, chiffres, tirets)."
  }
}

variable "compte_aws" {
  description = "Numéro du compte AWS propriétaire des clés."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{12}$", var.compte_aws))
    error_message = "Le numéro de compte AWS compte 12 chiffres."
  }
}

variable "region" {
  description = "Région AWS des clés (sert aux conditions kms:ViaService)."
  type        = string
}

variable "administrateurs" {
  description = <<-EOT
    ARN (motifs ArnLike acceptés) des principaux autorisés à administrer les clés. Aucun d'eux
    n'obtient l'usage cryptographique : celui-ci passe uniquement par les services AWS.
    L'utilisateur racine du compte est toujours ajouté (secours).
  EOT
  type        = list(string)

  validation {
    condition     = length(var.administrateurs) > 0 && alltrue([for a in var.administrateurs : startswith(a, "arn:aws:iam::")])
    error_message = "Au moins un administrateur, désigné par un ARN IAM."
  }
}

variable "periode_rotation_jours" {
  description = "Période de rotation automatique des clés, en jours (90 à 2560)."
  type        = number
  default     = 365

  validation {
    condition     = var.periode_rotation_jours >= 90 && var.periode_rotation_jours <= 2560
    error_message = "KMS accepte une période de rotation de 90 à 2560 jours."
  }
}

variable "delai_suppression_jours" {
  description = "Délai d'attente avant la suppression effective d'une clé, en jours (7 à 30)."
  type        = number
  default     = 30

  validation {
    condition     = var.delai_suppression_jours >= 7 && var.delai_suppression_jours <= 30
    error_message = "KMS accepte un délai de suppression de 7 à 30 jours."
  }
}
