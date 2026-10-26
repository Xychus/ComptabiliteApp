variable "prefixe" {
  description = "Préfixe des noms de ressources (ex. « ledger-dev »)."
  type        = string
}

variable "region" {
  description = "Région AWS (widgets du tableau de bord)."
  type        = string
}

variable "cle_kms_xray_arn" {
  description = "ARN de la CMK « xray » : chiffrement des traces du compte. Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_xray_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "cle_kms_journaux_arn" {
  description = "ARN de la CMK « logs » (métriques EMF des services). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_journaux_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "retention_journaux_jours" {
  description = "Conservation des métriques EMF brutes (jours) ; les métriques extraites suivent la rétention de CloudWatch."
  type        = number
  default     = 365
}

variable "suffixe_equilibreur" {
  description = "Suffixe d'ARN de l'équilibreur (dimension LoadBalancer)."
  type        = string
}

variable "suffixes_groupes_cibles" {
  description = "Suffixe d'ARN du groupe cible de chaque API."
  type        = map(string)
}

variable "instances_aurora" {
  description = "Identifiants des instances Aurora (retard du slot logique, retard CDC)."
  type        = list(string)
}
