variable "prefixe" {
  description = "Préfixe des noms de ressources (ex. « ledger-dev »)."
  type        = string
}

variable "region" {
  description = "Région AWS."
  type        = string
}

variable "compte_aws" {
  description = "Numéro du compte AWS."
  type        = string
}

variable "vpc_id" {
  description = "Identifiant du VPC."
  type        = string
}

variable "sous_reseaux" {
  description = "Sous-réseaux « applications » : autorizer et VPC Link."
  type        = list(string)
}

variable "groupe_securite_points_de_terminaison" {
  description = "Groupe des points de terminaison d'interface (SSM) : sortie HTTPS de l'autorizer."
  type        = string
}

variable "point_de_terminaison_execute_api" {
  description = "Point de terminaison execute-api du VPC : seule origine admise par la politique de l'API."
  type        = string
}

variable "equilibreur" {
  description = "Équilibreur interne : ARN (cible du VPC Link), groupe de sécurité, nom de son certificat public."
  type = object({
    arn             = string
    groupe_securite = string
    nom_hote        = string
  })
}

variable "keycloak" {
  description = "Keycloak : URL du realm (émetteur), groupe de sécurité et port HTTPS joints par l'autorizer."
  type = object({
    url_realm       = string
    groupe_securite = string
    port            = number
  })

  validation {
    condition     = startswith(var.keycloak.url_realm, "https://")
    error_message = "L'émetteur est une URL HTTPS."
  }
}

variable "audience" {
  description = "Audience exigée dans les jetons."
  type        = string
  default     = "ledger-api"
}

variable "certificat_racine_pki_pem" {
  description = "Racine de la PKI (PEM, publique) : seule autorité admise pour joindre Keycloak. null : avant la cérémonie."
  type        = string
  default     = null
}

variable "paquet" {
  description = "Paquet zip de l'autorizer publié par images.yml : bucket des artefacts et version SemVer exacte (ADR 0032)."
  type = object({
    bucket  = string
    version = string
  })

  validation {
    condition     = can(regex("^[0-9]+[.][0-9]+[.][0-9]+$", var.paquet.version))
    error_message = "Version SemVer exacte (ex. 0.1.0) : jamais « latest »."
  }
}

variable "duree_cache_autorisation_secondes" {
  description = "Durée de mise en cache d'une politique par jeton : au plus la durée de vie des jetons (300 s)."
  type        = number
  default     = 300

  validation {
    condition     = var.duree_cache_autorisation_secondes >= 0 && var.duree_cache_autorisation_secondes <= 300
    error_message = "Le cache de l'autorizer ne dépasse pas la durée de vie des jetons (300 s)."
  }
}

variable "debit_etape" {
  description = "Plafond de l'étape, tous clients confondus (requêtes par seconde et rafale)."
  type = object({
    debit  = number
    rafale = number
  })
  default = {
    debit  = 200
    rafale = 400
  }
}

variable "plans_usage" {
  description = <<-EOT
    Plans d'usage par catégorie de clients : clients (azp, clientId Keycloak), débit en lecture
    (GET, HEAD) et en écriture (PUT, POST), quota quotidien facultatif. Un client absent de tout
    plan est refusé (403) par l'autorizer.
  EOT
  type = map(object({
    clients         = list(string)
    lecture         = object({ debit = number, rafale = number })
    ecriture        = object({ debit = number, rafale = number })
    quota_quotidien = optional(number)
  }))

  validation {
    condition     = length(flatten([for p in values(var.plans_usage) : p.clients])) == length(distinct(flatten([for p in values(var.plans_usage) : p.clients])))
    error_message = "Un client appartient à un seul plan d'usage."
  }

  validation {
    condition     = alltrue(flatten([for p in values(var.plans_usage) : [for c in p.clients : can(regex("^[A-Za-z0-9._-]{1,128}$", c))]]))
    error_message = "Identifiant de client : lettres, chiffres, « . », « _ », « - » (128 au plus), comme l'exige l'autorizer."
  }
}

variable "retention_journaux_jours" {
  description = "Conservation des journaux d'accès, d'exécution et de l'autorizer, en jours."
  type        = number
  default     = 365

  validation {
    condition     = var.retention_journaux_jours >= 365
    error_message = "Les journaux d'accès sont conservés au moins un an."
  }
}

variable "cle_kms_journaux_arn" {
  description = "CMK « logs »."
  type        = string
}

variable "cle_kms_lambda_arn" {
  description = "CMK « lambda » : paquet et variables d'environnement de l'autorizer."
  type        = string
}

variable "cle_kms_ssm_arn" {
  description = "CMK « ssm » : clés des plans d'usage (SecureString)."
  type        = string
}

variable "frontiere_permissions_arn" {
  description = "Frontière de permissions des rôles /ledger/."
  type        = string
}
