variable "prefixe" {
  description = "Préfixe des noms de ressources (ex. « ledger-dev »)."
  type        = string
}

variable "nom" {
  description = "Nom du service (ex. « command-api ») : service ECS, famille, rôles, journaux."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,30}[a-z0-9]$", var.nom))
    error_message = "Nom en minuscules (lettres, chiffres, tirets)."
  }
}

variable "region" {
  description = "Région AWS."
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
  description = "Sous-réseaux « applications » des tâches."
  type        = list(string)
}

variable "arn_grappe" {
  description = "ARN de la grappe ECS."
  type        = string
}

variable "nom_grappe" {
  description = "Nom de la grappe ECS (mise à l'échelle, métriques)."
  type        = string
}

variable "groupe_securite_points_de_terminaison" {
  description = "Groupe des points de terminaison d'interface."
  type        = string
}

variable "depot_ecr_url" {
  description = "URL du dépôt ECR du service."
  type        = string
}

variable "depot_ecr_arn" {
  description = "ARN du dépôt ECR du service."
  type        = string
}

variable "version_image" {
  description = "Version SemVer exacte de l'image ; null tant qu'aucune image n'est publiée (aucun service créé)."
  type        = string
  default     = null

  validation {
    condition     = var.version_image == null || can(regex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$", var.version_image))
    error_message = "Une version SemVer exacte est exigée (ex. 0.3.1) ; « latest » est refusé."
  }
}

variable "commande" {
  description = "Arguments du conteneur (null : ceux de l'image)."
  type        = list(string)
  default     = null
}

variable "cpu" {
  description = "Unités de processeur de la tâche Fargate."
  type        = number
  default     = 512
}

variable "memoire" {
  description = "Mémoire de la tâche Fargate (Mio)."
  type        = number
  default     = 1024
}

variable "environnement" {
  description = "Variables d'environnement non secrètes (jamais un mot de passe, une clé ni une chaîne contenant un secret)."
  type        = map(string)
  default     = {}

  validation {
    condition     = alltrue([for nom, valeur in var.environnement : !can(regex("(?i)(password|mot_de_passe|secret|cle$|key$)", nom)) && !can(regex("(?i)password=", valeur))])
    error_message = "Une variable d'environnement en clair ne peut pas porter de secret : utiliser « secrets »."
  }
}

variable "secrets" {
  description = "Secrets injectés par ECS : nom de variable → valueFrom (ARN de secret, éventuellement « :champ:: »)."
  type        = map(string)
  default     = {}
}

variable "arn_secrets_lus" {
  description = "ARN des secrets lus par le rôle d'exécution (ceux de « secrets », sans suffixe de champ)."
  type        = list(string)
  default     = []
}

variable "port_https" {
  description = "Port HTTPS de Kestrel (certificat de la PKI interne) ; null pour un service sans écoute (worker)."
  type        = number
  default     = null
}

variable "groupe_cible_arn" {
  description = "Groupe cible de l'équilibreur de charge (null : aucun)."
  type        = string
  default     = null
}

variable "groupe_securite_equilibreur" {
  description = "Groupe de sécurité de l'équilibreur de charge, autorisé vers le port HTTPS du service."
  type        = string
  default     = null
}

variable "sorties" {
  description = "Destinations réseau du service : groupe de sécurité → port (ex. Aurora 5432, Kafka 9093). Le module pose aussi l'entrée sur le groupe cible."
  type = map(object({
    groupe_securite = string
    port            = number
  }))
  default = {}
}

variable "declarations_role_tache" {
  description = "Déclarations IAM supplémentaires du rôle de tâche (ex. rds-db:connect), au moindre privilège."
  type        = any
  default     = []
}

variable "nombre_taches" {
  description = "Nombre de tâches (minimum de la mise à l'échelle si elle est activée)."
  type        = number
  default     = 1
}

variable "mise_a_echelle" {
  description = "Mise à l'échelle automatique sur l'utilisation du processeur (null : nombre fixe)."
  type = object({
    maximum     = number
    cible_cpu   = optional(number, 60)
    temps_repos = optional(number, 120)
  })
  default = null
}

variable "depot_collecteur_url" {
  description = "Dépôt ECR de la copie de l'image officielle ADOT (aws-otel-collector, ADR 0028)."
  type        = string
}

variable "depot_collecteur_arn" {
  description = "ARN du dépôt ECR du collecteur ADOT."
  type        = string
}

variable "version_collecteur" {
  description = "Étiquette exacte du collecteur ADOT."
  type        = string
  default     = "v0.50.0"

  validation {
    condition     = can(regex("^v[0-9]+\\.[0-9]+\\.[0-9]+$", var.version_collecteur))
    error_message = "Une version exacte est exigée (ex. v0.50.0)."
  }
}

variable "groupe_journaux_metriques" {
  description = "Groupe de journaux (CMK « logs ») où le collecteur écrit les métriques au format EMF."
  type        = string
}

variable "cle_kms_secrets_arn" {
  description = "ARN de la CMK « secrets ». Jamais une clé aws/*."
  type        = string
}

variable "cle_kms_journaux_arn" {
  description = "ARN de la CMK « logs ». Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_journaux_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "cle_kms_xray_arn" {
  description = "ARN de la CMK « xray » (traces chiffrées). Jamais une clé aws/*."
  type        = string
}

variable "frontiere_permissions_arn" {
  description = "ARN de la frontière de permissions des rôles créés par le pipeline."
  type        = string
}

variable "retention_journaux_jours" {
  description = "Conservation des journaux du service (jours)."
  type        = number
  default     = 365
}
