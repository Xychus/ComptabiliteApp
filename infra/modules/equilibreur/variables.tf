variable "prefixe" {
  description = "Préfixe des noms de ressources (ex. « ledger-dev »)."
  type        = string
}

variable "vpc_id" {
  description = "Identifiant du VPC."
  type        = string
}

variable "cidr_vpc" {
  description = "Bloc CIDR du VPC : seule origine admise sur HTTPS (VPC Link d'API Gateway en phase 9)."
  type        = string
}

variable "sous_reseaux" {
  description = "Sous-réseaux « applications » de l'équilibreur interne."
  type        = list(string)
}

variable "zone_interne" {
  description = "Zone DNS privée du VPC (identifiant et domaine) : l'équilibreur y est publié sous api.<domaine>."
  type = object({
    id      = string
    domaine = string
  })
}

variable "port_cibles" {
  description = "Port HTTPS de Kestrel sur les cibles."
  type        = number
  default     = 8443
}

variable "chemin_sante" {
  description = "Sonde de disponibilité des cibles."
  type        = string
  default     = "/health/ready"
}

variable "protection_suppression" {
  description = "Protection contre la suppression de l'équilibreur (false : dev jetable, ADR 0027)."
  type        = bool
  default     = true
}
