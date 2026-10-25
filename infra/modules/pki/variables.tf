variable "prefixe" {
  description = "Préfixe des noms de ressources (ex. « ledger-dev »)."
  type        = string
}

variable "region" {
  description = "Région AWS (conditions des politiques)."
  type        = string
}

variable "compte_aws" {
  description = "Numéro du compte AWS."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{12}$", var.compte_aws))
    error_message = "Le numéro de compte AWS compte 12 chiffres."
  }
}

variable "vpc_id" {
  description = "Identifiant du VPC."
  type        = string
}

variable "sous_reseaux" {
  description = "Sous-réseaux « applications » de la Lambda."
  type        = list(string)
}

variable "groupe_securite_points_de_terminaison" {
  description = "Groupe des points de terminaison d'interface (Secrets Manager, KMS, ACM, ECS, CloudWatch)."
  type        = string
}

variable "depot_ecr_url" {
  description = "URL du dépôt ECR de l'image de la Lambda (« pki-lambda »)."
  type        = string
}

variable "version_image" {
  description = "Version SemVer exacte de l'image de la Lambda ; null tant qu'aucune image n'est publiée (aucune Lambda créée)."
  type        = string
  default     = null

  validation {
    condition     = var.version_image == null || can(regex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$", var.version_image))
    error_message = "Une version SemVer exacte est exigée (ex. 0.3.1) ; « latest » est refusé."
  }
}

variable "generations_intermediaire" {
  description = "Générations de la CA intermédiaire (ex. [\"2026\"]) : une clé KMS ECC P-384 par génération, gardée jusqu'à l'échéance de ses derniers certificats."
  type        = set(string)
  default     = []

  validation {
    condition     = alltrue([for g in var.generations_intermediaire : can(regex("^[0-9a-z-]{1,20}$", g))])
    error_message = "Une génération est un court identifiant (chiffres, minuscules, tirets), ex. « 2026 »."
  }
}

variable "generation_active" {
  description = "Génération dont la clé signe les certificats finaux ; null avant la première cérémonie (mode cérémonie)."
  type        = string
  default     = null

  validation {
    condition     = var.generation_active == null || contains(var.generations_intermediaire, coalesce(var.generation_active, "-"))
    error_message = "La génération active doit figurer dans generations_intermediaire."
  }
}

variable "certificat_racine_pem" {
  description = "Certificat de la racine hors ligne (PEM, public) ; null avant la première cérémonie."
  type        = string
  default     = null
}

variable "certificat_intermediaire_pem" {
  description = "Certificat de l'intermédiaire active, signé par la racine (PEM, public) ; null avant la première cérémonie."
  type        = string
  default     = null

  validation {
    condition     = (var.certificat_intermediaire_pem == null) == (var.certificat_racine_pem == null) && (var.certificat_intermediaire_pem == null) == (var.generation_active == null)
    error_message = "Certificat intermédiaire, certificat racine et génération active vont ensemble (tous ou aucun)."
  }
}

variable "identites" {
  description = <<-EOT
    Certificats finaux, un secret chacun (clé = nom court du secret). nom_commun : identité des ACL
    Kafka ; noms_dns : SAN (obligatoire pour un usage serveur) ; usage : serveur, client ou
    serveur-client ; services : services ECS de la grappe redéployés après chaque rotation ;
    certificat_acm_arn : certificat ACM réimporté après chaque rotation (load balancer).
  EOT
  type = map(object({
    nom_commun         = string
    noms_dns           = optional(list(string), [])
    usage              = string
    services           = optional(list(string), [])
    certificat_acm_arn = optional(string)
  }))
  default = {}

  validation {
    condition     = alltrue([for i in values(var.identites) : contains(["serveur", "client", "serveur-client"], i.usage)])
    error_message = "L'usage est « serveur », « client » ou « serveur-client »."
  }

  validation {
    condition     = alltrue([for i in values(var.identites) : i.usage == "client" || length(i.noms_dns) > 0])
    error_message = "Un certificat serveur exige au moins un nom DNS."
  }

  validation {
    condition     = alltrue([for i in values(var.identites) : length(join(" ", i.noms_dns)) <= 256 && length(join(" ", i.services)) <= 256])
    error_message = "Les noms DNS et les services, séparés par des espaces, tiennent dans une étiquette (256 caractères)."
  }
}

variable "nom_grappe" {
  description = "Nom de la grappe ECS des services redéployés après rotation."
  type        = string
}

variable "jours_entre_rotations" {
  description = "Intervalle de rotation des certificats finaux (valables 7 jours) : la marge sert à détecter un échec."
  type        = number
  default     = 5

  validation {
    condition     = var.jours_entre_rotations >= 1 && var.jours_entre_rotations <= 5
    error_message = "La rotation a lieu au plus tous les 5 jours (certificats de 7 jours, au moins 2 jours de marge)."
  }
}

variable "administrateurs_kms" {
  description = "ARN des principaux qui administrent les clés de l'intermédiaire (sans droit de signature)."
  type        = list(string)
}

variable "cle_kms_secrets_arn" {
  description = "ARN de la CMK « secrets » (secrets des certificats finaux). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_secrets_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "cle_kms_journaux_arn" {
  description = "ARN de la CMK « logs » (journaux de la Lambda). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_journaux_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "cle_kms_lambda_arn" {
  description = "ARN de la CMK « lambda » (variables d'environnement). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_lambda_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "frontiere_permissions_arn" {
  description = "ARN de la frontière de permissions imposée aux rôles créés par le pipeline (amorçage)."
  type        = string
}

variable "retention_journaux_jours" {
  description = "Conservation des journaux de la Lambda (trace de chaque émission), en jours."
  type        = number
  default     = 365

  validation {
    condition     = var.retention_journaux_jours >= 365
    error_message = "Les journaux d'émission sont conservés au moins un an."
  }
}

variable "delai_suppression_cles_jours" {
  description = "Délai avant la suppression effective d'une clé d'intermédiaire retirée (7 à 30 jours)."
  type        = number
  default     = 30
}

variable "fenetre_recuperation_secrets_jours" {
  description = "Fenêtre de récupération d'un secret supprimé (0 = immédiat, dev jetable ; sinon 7 à 30)."
  type        = number
  default     = 30
}
