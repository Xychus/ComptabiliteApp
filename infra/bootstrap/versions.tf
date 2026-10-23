terraform {
  required_version = ">= 1.11"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.70"
    }
  }

  # Configuration partielle (ADR 0011) : bucket, région et clé KMS sont passés à l'init
  # (voir README.md). Le premier apply se fait avec un état local, puis on migre l'état ici.
  backend "s3" {
    key          = "bootstrap/terraform.tfstate"
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
      environnement = var.environnement
      composant     = "bootstrap"
      gere_par      = "terraform"
    }
  }
}
