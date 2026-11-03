variable "compte_aws" {
  description = "Numéro du compte AWS de l'environnement (fourni par le pipeline : TF_VAR_compte_aws)."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{12}$", var.compte_aws))
    error_message = "Le numéro de compte AWS compte 12 chiffres."
  }
}

variable "region" {
  description = "Région AWS de l'environnement."
  type        = string
  default     = "ca-central-1"
}

variable "zones" {
  description = "Les trois zones de disponibilité de l'environnement (liste figée : aucun ajout silencieux par AWS)."
  type        = list(string)
  default     = ["ca-central-1a", "ca-central-1b", "ca-central-1d"]
}

variable "cidr_vpc" {
  description = "Bloc CIDR du VPC de l'environnement."
  type        = string
  default     = "10.20.0.0/16"
}

variable "administrateurs_kms" {
  description = "Administrateurs des CMK en plus du rôle gha-apply et de l'utilisateur racine (motifs ArnLike)."
  type        = list(string)
  default     = []
}

variable "capacite_aurora" {
  description = "Capacité Aurora Serverless v2 (ACU). Le rédacteur ne se met jamais en pause (réplication logique) ; le lecteur, si (ADR 0027)."
  type = object({
    minimum_acu          = number
    maximum_acu          = number
    secondes_avant_pause = optional(number, 300)
  })
  default = {
    minimum_acu          = 0
    maximum_acu          = 4
    secondes_avant_pause = 900
  }
}

variable "version_image_migrations" {
  description = <<-EOT
    Étiquette SemVer exacte de l'image des migrations. Fournie par le déploiement (deploy.yml,
    phase 10) ; d'ici là, aucune image n'est publiée et la tâche n'est pas lancée.
  EOT
  type        = string
  default     = "0.1.0"
}

# --- Exécution (phase 8) -------------------------------------------------------------------

variable "versions_images" {
  description = <<-EOT
    Versions exactes des images publiées par images.yml (ADR 0028). null : composant sans service
    (aucune image publiée). pki : Lambda et sentinelle ; kafka, debezium, keycloak, collecteur : copies
    des images officielles tierces (étiquette de l'éditeur).
  EOT
  type = object({
    pki         = optional(string)
    command     = optional(string)
    query       = optional(string)
    projections = optional(string)
    migrations  = optional(string)
    kafka       = optional(string, "4.3.2")
    debezium    = optional(string, "3.7.0.Final")
    keycloak    = optional(string, "26.8.1")
    collecteur  = optional(string, "v0.50.0")
  })
  default = {}
}

variable "generations_intermediaire" {
  description = "Générations de la CA intermédiaire (une clé KMS chacune), ex. [\"2026\"] (runbook pki-ceremonie-annuelle)."
  type        = set(string)
  default     = []
}

variable "generation_active" {
  description = "Génération active de l'intermédiaire ; utilisée dès que pki/intermediaire.pem est versionné."
  type        = string
  default     = null
}

variable "identifiant_grappe_kraft" {
  description = "Identifiant KRaft de la grappe Kafka (UUID base64url), fixé une fois pour toutes."
  type        = string
  default     = "0TqLSlDtQHOLxtu682Oxmg"
}

variable "kafka" {
  description = "Dimensionnement des nœuds Kafka."
  type = object({
    type_instance    = string
    memoire_mio      = number
    taille_volume_go = number
  })
  default = {
    type_instance    = "t3.large"
    memoire_mio      = 2048
    taille_volume_go = 20
  }
}

variable "services" {
  description = "Nombre de tâches des services : minimum et maximum des API (mise à l'échelle), worker."
  type = object({
    taches_minimum = number
    taches_maximum = number
    taches_worker  = number
    # Keycloak (ADR 0030) : grappe par jdbc-ping au-delà d'une tâche.
    taches_keycloak = optional(number, 1)
  })
  default = {
    taches_minimum = 1
    taches_maximum = 2
    taches_worker  = 1
  }
}

variable "protection_suppression" {
  description = "Protection contre la suppression de l'équilibreur de charge."
  type        = bool
  default     = false
}

variable "delai_suppression_cles_pki_jours" {
  description = "Délai avant la suppression d'une clé d'intermédiaire retirée."
  type        = number
  default     = 7
}

variable "fenetre_recuperation_secrets_jours" {
  description = "Fenêtre de récupération des secrets de la PKI supprimés (0 : immédiat)."
  type        = number
  default     = 0
}

# --- Entrée (phase 9, ADR 0031) --------------------------------------------------------------

variable "domaine_parent" {
  description = "Domaine public parent : la zone du compte (amorçage) est « <environnement>.<domaine_parent> »."
  type        = string
  default     = "aws.usinageone.ca"
}

variable "version_autorizer" {
  description = <<-EOT
    Version SemVer exacte du paquet de l'autorizer publié par images.yml (src/Authorizer/version.txt,
    ADR 0032). null : paquet non publié, aucune API Gateway (l'équilibreur reste sans entrée).
  EOT
  type        = string
  default     = null
}

variable "plans_usage" {
  description = <<-EOT
    Plans d'usage par catégorie de clients (clientId Keycloak = azp) : débit en lecture (GET, HEAD)
    et en écriture (PUT, POST), en requêtes par seconde et en rafale ; quota quotidien facultatif.
  EOT
  type = map(object({
    clients         = list(string)
    lecture         = object({ debit = number, rafale = number })
    ecriture        = object({ debit = number, rafale = number })
    quota_quotidien = optional(number)
  }))
  default = {
    exemples = {
      clients  = ["ledger-exemple-ecriture", "ledger-exemple-lecture"]
      lecture  = { debit = 50, rafale = 100 }
      ecriture = { debit = 10, rafale = 20 }
    }
  }
}
