variable "prefixe" {
  description = "Préfixe des noms de ressources (ex. « ledger-dev »)."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,40}[a-z0-9]$", var.prefixe))
    error_message = "Le préfixe doit être en minuscules (lettres, chiffres, tirets)."
  }
}

variable "vpc_id" {
  description = "Identifiant du VPC."
  type        = string
}

variable "sous_reseaux" {
  description = "Sous-réseaux « donnees », un par zone (au moins deux zones)."
  type        = list(string)

  validation {
    condition     = length(var.sous_reseaux) >= 2
    error_message = "Aurora exige des sous-réseaux dans au moins deux zones."
  }
}

variable "version_moteur" {
  description = "Version d'Aurora PostgreSQL (P4 : version majeure 18). Montée de version mineure par PR, jamais automatique."
  type        = string
  default     = "18.6"

  validation {
    condition     = can(regex("^18\\.[0-9]+$", var.version_moteur))
    error_message = "Version majeure 18 attendue (famille de paramètres aurora-postgresql18) ; changer de majeure exige une nouvelle famille."
  }
}

variable "classe_instance" {
  description = "Classe des instances (Performance Insights doit y être disponible)."
  type        = string
  default     = "db.r7g.large"
}

variable "nombre_instances" {
  description = "Nombre d'instances : un rédacteur et au moins un lecteur, cible de basculement."
  type        = number
  default     = 2

  validation {
    condition     = var.nombre_instances >= 2
    error_message = "Au moins deux instances : sans lecteur, aucun basculement n'est possible."
  }
}

variable "nom_base" {
  description = "Nom de la base créée avec la grappe."
  type        = string
  default     = "ledger"
}

variable "utilisateur_maitre" {
  description = "Nom de l'utilisateur maître (propriétaire du schéma, rôle des migrations)."
  type        = string
  default     = "ledger_admin"
}

variable "cle_kms_aurora_arn" {
  description = "ARN de la CMK « aurora » (stockage, Performance Insights). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_aurora_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "cle_kms_secrets_arn" {
  description = "ARN de la CMK « secrets » (secret du mot de passe maître). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_secrets_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "cle_kms_journaux_arn" {
  description = "ARN de la CMK « logs » (journaux PostgreSQL exportés). Jamais une clé aws/*."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]+$", var.cle_kms_journaux_arn))
    error_message = "Un ARN de clé KMS (key/…) est exigé ; un alias, en particulier aws/*, est refusé."
  }
}

variable "retention_sauvegardes_jours" {
  description = "Conservation des sauvegardes automatiques, fenêtre de restauration à un instant donné (PITR)."
  type        = number
  default     = 35

  validation {
    condition     = var.retention_sauvegardes_jours >= 7 && var.retention_sauvegardes_jours <= 35
    error_message = "Aurora accepte de 1 à 35 jours ; au moins 7 sont exigés."
  }
}

variable "retention_journaux_jours" {
  description = "Durée de conservation des journaux PostgreSQL exportés, en jours."
  type        = number
  default     = 365

  validation {
    condition     = var.retention_journaux_jours >= 365
    error_message = "Les journaux de la base sont conservés au moins un an."
  }
}

variable "seuils_alarmes" {
  description = "Seuils des alarmes CloudWatch."
  type = object({
    volume_utilise_octets       = number # VolumeBytesUsed (grappe)
    stockage_local_libre        = number # FreeLocalStorage (instance), en octets
    connexions                  = number # DatabaseConnections (instance)
    retard_slot_octets          = number # OldestReplicationSlotLag (instance)
    disque_slots_octets         = number # ReplicationSlotDiskUsage (instance)
    periodes_retard_slot_alerte = number # périodes de 5 min consécutives au-dessus du seuil
  })
  default = {
    volume_utilise_octets       = 107374182400 # 100 Gio
    stockage_local_libre        = 5368709120   # 5 Gio
    connexions                  = 400
    retard_slot_octets          = 1073741824 # 1 Gio
    disque_slots_octets         = 5368709120 # 5 Gio
    periodes_retard_slot_alerte = 3
  }
}

variable "actions_alarmes" {
  description = "ARN notifiés au déclenchement et au retour à la normale des alarmes (sujet SNS : phase 11)."
  type        = list(string)
  default     = []
}
