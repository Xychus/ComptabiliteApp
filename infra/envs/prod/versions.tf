terraform {
  required_version = ">= 1.11"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.70"
    }
  }

  # Configuration partielle (ADR 0011) : bucket, région et clé KMS de l'état du compte sont
  # passés à l'init par le pipeline (infra-ci.yml), à partir du seul numéro de compte.
  backend "s3" {
    key          = "envs/prod/terraform.tfstate"
    encrypt      = true
    use_lockfile = true
  }
}

provider "aws" {
  region              = var.region
  allowed_account_ids = [var.compte_aws]

  default_tags {
    tags = {
      projet        = "ledger"
      environnement = local.environnement
      composant     = "infra"
      gere_par      = "terraform"
    }
  }
}
