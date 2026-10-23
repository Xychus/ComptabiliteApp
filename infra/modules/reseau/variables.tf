variable "prefixe" {
  description = "Préfixe des noms de ressources (ex. « ledger-dev »)."
  type        = string
}

variable "region" {
  description = "Région AWS (sert aux noms des services des points de terminaison VPC)."
  type        = string
}

variable "compte_aws" {
  description = "Numéro du compte AWS (condition aws:SourceAccount du rôle des journaux de flux)."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{12}$", var.compte_aws))
    error_message = "Le numéro de compte AWS compte 12 chiffres."
  }
}

variable "cidr_vpc" {
  description = "Bloc CIDR du VPC (/16 recommandé : chaque sous-réseau en prend 1/16)."
  type        = string

  validation {
    condition     = can(cidrhost(var.cidr_vpc, 0)) && tonumber(split("/", var.cidr_vpc)[1]) <= 20
    error_message = "Le CIDR du VPC doit être valide et au plus /20."
  }
}

variable "zones" {
  description = "Exactement trois zones de disponibilité, dans un ordre stable."
  type        = list(string)

  validation {
    condition     = length(var.zones) == 3 && length(distinct(var.zones)) == 3
    error_message = "Trois zones de disponibilité distinctes sont exigées."
  }
}

variable "cle_kms_journaux_arn" {
  description = "ARN de la CMK du domaine « logs » (journaux de flux VPC). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_journaux_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "retention_journaux_jours" {
  description = "Durée de conservation des journaux de flux VPC, en jours (au moins un an)."
  type        = number
  default     = 365

  validation {
    condition     = var.retention_journaux_jours >= 365
    error_message = "Les journaux de flux sont conservés au moins un an."
  }
}

variable "frontiere_permissions_arn" {
  description = "ARN de la frontière de permissions imposée aux rôles créés par le pipeline (amorçage)."
  type        = string
}

variable "services_points_de_terminaison" {
  description = "Services AWS joints par un point de terminaison d'interface (suffixe de com.amazonaws.<région>.)."
  type        = set(string)
  default     = ["kms", "secretsmanager", "ssm", "ecr.api", "ecr.dkr", "logs", "sts", "execute-api"]
}
