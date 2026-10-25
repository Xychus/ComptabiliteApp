variable "prefixe" {
  description = "Nom de la grappe (ex. « ledger-dev ») ; la CMK « ecs » n'accepte que les grappes « ledger-* »."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,40}[a-z0-9]$", var.prefixe))
    error_message = "Le nom doit être en minuscules (lettres, chiffres, tirets)."
  }
}

variable "cle_kms_ecs_arn" {
  description = "ARN de la CMK « ecs » (stockage éphémère des tâches Fargate). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_ecs_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}
