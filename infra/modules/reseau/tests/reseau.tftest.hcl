# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {
  mock_resource "aws_cloudwatch_log_group" {
    defaults = {
      arn = "arn:aws:logs:ca-central-1:111122223333:log-group:/ledger-dev/vpc/journaux-de-flux"
    }
  }

  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/ledger/ledger-dev-journaux-de-flux"
    }
  }
}

variables {
  prefixe                   = "ledger-dev"
  region                    = "ca-central-1"
  compte_aws                = "111122223333"
  cidr_vpc                  = "10.20.0.0/16"
  zones                     = ["ca-central-1a", "ca-central-1b", "ca-central-1d"]
  cle_kms_journaux_arn      = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-1111-4a5b-9c3d-0123456789ab"
  frontiere_permissions_arn = "arn:aws:iam::111122223333:policy/bootstrap/ledger-frontiere-charges"
}

run "sous_reseaux_prives_sur_trois_zones" {
  command = apply

  assert {
    condition     = length(aws_subnet.sous_reseau) == 6 && length(distinct([for s in aws_subnet.sous_reseau : s.availability_zone])) == 3
    error_message = "Deux niveaux de sous-réseaux sur trois zones sont attendus."
  }

  assert {
    condition     = alltrue([for s in aws_subnet.sous_reseau : !s.map_public_ip_on_launch])
    error_message = "Aucun sous-réseau ne doit attribuer d'adresse publique."
  }

  assert {
    condition     = length(distinct([for s in aws_subnet.sous_reseau : s.cidr_block])) == 6 && aws_subnet.sous_reseau["donnees-2"].cidr_block == "10.20.160.0/20"
    error_message = "Les blocs CIDR des sous-réseaux doivent être distincts et découpés en /20."
  }

  # Aucune route sortante : ni passerelle Internet ni NAT (la seule route est locale).
  assert {
    condition     = alltrue([for t in aws_route_table.niveau : length(t.route) == 0])
    error_message = "Les tables de routage ne doivent porter aucune route vers Internet."
  }
}

run "points_de_terminaison_prives" {
  command = apply

  assert {
    condition     = toset(keys(aws_vpc_endpoint.interface)) == toset(["kms", "secretsmanager", "ssm", "ecr.api", "ecr.dkr", "logs", "sts", "execute-api"])
    error_message = "Les points de terminaison d'interface exigés par le plan sont absents."
  }

  assert {
    condition     = alltrue([for e in aws_vpc_endpoint.interface : e.private_dns_enabled && length(e.subnet_ids) == 3])
    error_message = "Chaque point de terminaison d'interface doit avoir le DNS privé dans les trois zones."
  }

  assert {
    condition     = aws_vpc_endpoint.s3.vpc_endpoint_type == "Gateway" && length(aws_vpc_endpoint.s3.route_table_ids) == 2
    error_message = "La passerelle S3 doit desservir les deux tables de routage."
  }

  assert {
    condition     = aws_vpc_security_group_ingress_rule.points_de_terminaison_https.from_port == 443 && aws_vpc_security_group_ingress_rule.points_de_terminaison_https.cidr_ipv4 == "10.20.0.0/16"
    error_message = "Seul HTTPS depuis le VPC doit atteindre les points de terminaison."
  }

  assert {
    condition     = length(aws_default_security_group.defaut.ingress) == 0 && length(aws_default_security_group.defaut.egress) == 0
    error_message = "Le groupe de sécurité par défaut doit être vide."
  }
}

run "journaux_de_flux_chiffres_par_cmk" {
  command = apply

  assert {
    condition     = aws_cloudwatch_log_group.journaux_de_flux.kms_key_id == var.cle_kms_journaux_arn && aws_cloudwatch_log_group.journaux_de_flux.retention_in_days == 365
    error_message = "Les journaux de flux doivent être chiffrés par la CMK « logs » et conservés un an."
  }

  assert {
    condition     = aws_flow_log.vpc.traffic_type == "ALL" && aws_flow_log.vpc.log_destination == aws_cloudwatch_log_group.journaux_de_flux.arn
    error_message = "Tout le trafic du VPC doit être journalisé dans le groupe chiffré."
  }

  assert {
    condition     = aws_iam_role.journaux_de_flux.permissions_boundary == var.frontiere_permissions_arn && aws_iam_role.journaux_de_flux.path == "/ledger/"
    error_message = "Le rôle des journaux de flux doit porter la frontière de permissions et le chemin /ledger/."
  }
}

run "refuse_une_cle_geree_par_aws" {
  command = plan

  variables {
    cle_kms_journaux_arn = "arn:aws:kms:ca-central-1:111122223333:alias/aws/logs"
  }

  expect_failures = [var.cle_kms_journaux_arn]
}

run "refuse_moins_de_trois_zones" {
  command = plan

  variables {
    zones = ["ca-central-1a", "ca-central-1b"]
  }

  expect_failures = [var.zones]
}
