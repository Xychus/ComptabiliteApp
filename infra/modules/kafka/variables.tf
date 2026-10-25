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
  description = "Sous-réseaux « donnees », un par zone, dans l'ordre : le nœud kafka-N est dans le N-ième."
  type        = list(string)

  validation {
    condition     = length(var.sous_reseaux) == 3
    error_message = "Trois sous-réseaux (un nœud par zone) sont exigés."
  }
}

variable "sous_reseaux_applications" {
  description = "Sous-réseaux « applications » de la tâche d'initialisation (Fargate)."
  type        = list(string)
}

variable "domaine_interne" {
  description = "Domaine privé du VPC (ex. « ledger-dev.internal ») : les nœuds sont kafka-N.kafka.<domaine>."
  type        = string
}

variable "nom_grappe" {
  description = "Nom de la grappe ECS."
  type        = string
}

variable "arn_grappe" {
  description = "ARN de la grappe ECS."
  type        = string
}

variable "groupe_securite_points_de_terminaison" {
  description = "Groupe des points de terminaison d'interface."
  type        = string
}

variable "identifiant_grappe_kraft" {
  description = "Identifiant KRaft de la grappe (UUID en base64url, 22 caractères), fixé une fois pour toutes par environnement."
  type        = string

  validation {
    condition     = can(regex("^[A-Za-z0-9_-]{22}$", var.identifiant_grappe_kraft))
    error_message = "Un UUID encodé en base64url sans remplissage (22 caractères) est exigé."
  }
}

variable "depot_kafka_url" {
  description = "Dépôt ECR de la copie de l'image officielle apache/kafka (ADR 0028)."
  type        = string
}

variable "depot_kafka_arn" {
  description = "ARN du dépôt ECR de l'image apache/kafka."
  type        = string
}

variable "version_kafka" {
  description = "Étiquette exacte de l'image apache/kafka (la même qu'en local et en test)."
  type        = string
  default     = "4.3.2"

  validation {
    condition     = can(regex("^[0-9]+\\.[0-9]+\\.[0-9]+$", var.version_kafka))
    error_message = "Une version exacte est exigée (ex. 4.3.2) ; « latest » est refusé."
  }
}

variable "depot_sentinelle_url" {
  description = "Dépôt ECR de l'image de la sentinelle (pki-sentinelle)."
  type        = string
}

variable "depot_sentinelle_arn" {
  description = "ARN du dépôt ECR de la sentinelle."
  type        = string
}

variable "version_sentinelle" {
  description = "Version SemVer exacte de la sentinelle ; null tant qu'aucune image n'est publiée (aucun service créé)."
  type        = string
  default     = null

  validation {
    condition     = var.version_sentinelle == null || can(regex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$", var.version_sentinelle))
    error_message = "Une version SemVer exacte est exigée ; « latest » est refusé."
  }
}

variable "secrets_noeuds" {
  description = "ARN du secret du certificat de chaque nœud (clés kafka-1, kafka-2, kafka-3), module pki."
  type        = map(string)

  validation {
    condition     = toset(keys(var.secrets_noeuds)) == toset(["kafka-1", "kafka-2", "kafka-3"])
    error_message = "Les secrets des trois nœuds kafka-1, kafka-2 et kafka-3 sont exigés."
  }
}

variable "secret_administration" {
  description = "ARN du secret du certificat « kafka-admin » (tâche d'initialisation : sujets et ACL)."
  type        = string
}

variable "acl" {
  description = <<-EOT
    Droits des identités clientes (nom commun du certificat). sujets_ecriture / sujets_lecture : sujets
    exacts ; prefixes_sujets : préfixes (lecture, écriture, description, création) ; groupes : groupes
    de consommateurs lus.
  EOT
  type = map(object({
    sujets_ecriture = optional(list(string), [])
    sujets_lecture  = optional(list(string), [])
    prefixes_sujets = optional(list(string), [])
    groupes         = optional(list(string), [])
  }))
  default = {}
}

variable "partitions_evenements" {
  description = "Partitions du sujet ledger.events (ordre garanti par clé d'entité, ADR 0020)."
  type        = number
  default     = 6
}

variable "type_instance" {
  description = "Type d'instance EC2 des nœuds."
  type        = string
  default     = "m7i.large"
}

variable "memoire_kafka_mio" {
  description = "Mémoire réservée au conteneur Kafka (Mio) ; le tas Java en prend la moitié."
  type        = number
  default     = 4096
}

variable "taille_volume_go" {
  description = "Taille du volume EBS de données de chaque nœud (Gio, gp3)."
  type        = number
  default     = 100
}

variable "seuil_retard_projections" {
  description = "Retard (messages) du groupe ledger-projections au-delà duquel l'alarme se déclenche."
  type        = number
  default     = 1000
}

variable "cle_kms_kafka_arn" {
  description = "ARN de la CMK « kafka » (volumes EBS des nœuds). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_kafka_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "cle_kms_secrets_arn" {
  description = "ARN de la CMK « secrets » (lecture des certificats par les sentinelles). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_secrets_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "cle_kms_journaux_arn" {
  description = "ARN de la CMK « logs ». Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_journaux_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "frontiere_permissions_arn" {
  description = "ARN de la frontière de permissions des rôles créés par le pipeline."
  type        = string
}

variable "retention_journaux_jours" {
  description = "Conservation des journaux des brokers (jours)."
  type        = number
  default     = 365
}
