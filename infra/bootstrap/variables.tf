variable "compte_aws" {
  description = "Numéro du compte AWS amorcé (un amorçage par compte : dev, prod)."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{12}$", var.compte_aws))
    error_message = "Le numéro de compte AWS compte 12 chiffres."
  }
}

variable "region" {
  description = "Région AWS du compte."
  type        = string
  default     = "ca-central-1"
}

variable "environnement" {
  description = "Environnement porté par ce compte ; c'est aussi le nom de l'environnement GitHub autorisé à appliquer."
  type        = string

  validation {
    condition     = contains(["dev", "prod"], var.environnement)
    error_message = "L'environnement est « dev » ou « prod »."
  }
}

variable "organisation_github" {
  description = "Organisation (ou utilisateur) GitHub propriétaire du dépôt."
  type        = string
}

variable "depot_github" {
  description = "Nom du dépôt GitHub."
  type        = string
}

variable "id_organisation_github" {
  description = <<-EOT
    Identifiant numérique du propriétaire du dépôt, si le dépôt émet des sujets OIDC immuables
    (« use_immutable_subject » : gh api repos/<org>/<dépôt>/actions/oidc/customization/sub).
    Laisser null pour le format par défaut « repo:<org>/<dépôt> ».
  EOT
  type        = number
  default     = null
}

variable "id_depot_github" {
  description = "Identifiant numérique du dépôt (sujets OIDC immuables) ; fourni avec id_organisation_github."
  type        = number
  default     = null

  validation {
    condition     = (var.id_depot_github == null) == (var.id_organisation_github == null)
    error_message = "id_organisation_github et id_depot_github se fournissent ensemble."
  }
}

variable "administrateurs" {
  description = <<-EOT
    ARN (motifs ArnLike acceptés) des administrateurs humains : ils appliquent l'amorçage,
    administrent la clé de l'état et lisent l'état. Ex. le rôle SSO d'administration :
    arn:aws:iam::<compte>:role/aws-reserved/sso.amazonaws.com/*/AWSReservedSSO_AdministratorAccess_*
  EOT
  type        = list(string)

  validation {
    condition     = length(var.administrateurs) > 0 && alltrue([for a in var.administrateurs : startswith(a, "arn:aws:iam::")])
    error_message = "Au moins un administrateur, désigné par un ARN IAM."
  }
}

variable "conservation_versions_etat_jours" {
  description = "Durée de conservation des versions antérieures des fichiers d'état, en jours."
  type        = number
  default     = 365
}
