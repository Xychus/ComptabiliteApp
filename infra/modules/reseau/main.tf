# VPC entièrement privé : aucun sous-réseau public, aucune passerelle Internet ni NAT. Les
# services AWS sont joints par des points de terminaison VPC (CLAUDE.md, « Sécurité »).
# Deux niveaux de sous-réseaux sur trois zones : « applications » (ECS, Lambda, points de
# terminaison) et « donnees » (Aurora, MSK).

locals {
  niveaux = {
    applications = 0
    donnees      = 8
  }

  # Clé « <niveau>-<indice de zone> » ; chaque sous-réseau occupe 1/16 du VPC.
  sous_reseaux = merge([
    for niveau, decalage in local.niveaux : {
      for i, zone in var.zones : "${niveau}-${i}" => {
        niveau = niveau
        zone   = zone
        cidr   = cidrsubnet(var.cidr_vpc, 4, decalage + i)
      }
    }
  ]...)
}

resource "aws_vpc" "vpc" {
  cidr_block           = var.cidr_vpc
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = {
    Name = var.prefixe
  }
}

# Groupe de sécurité par défaut vidé de toute règle : rien ne doit s'y rattacher par mégarde.
resource "aws_default_security_group" "defaut" {
  vpc_id = aws_vpc.vpc.id

  tags = {
    Name = "${var.prefixe}-defaut-ferme"
  }
}

resource "aws_subnet" "sous_reseau" {
  for_each = local.sous_reseaux

  vpc_id                  = aws_vpc.vpc.id
  cidr_block              = each.value.cidr
  availability_zone       = each.value.zone
  map_public_ip_on_launch = false

  tags = {
    Name   = "${var.prefixe}-${each.value.niveau}-${each.value.zone}"
    niveau = each.value.niveau
  }
}

# Une table de routage par niveau : uniquement la route locale (et la passerelle S3).
resource "aws_route_table" "niveau" {
  for_each = local.niveaux

  vpc_id = aws_vpc.vpc.id

  tags = {
    Name = "${var.prefixe}-${each.key}"
  }
}

resource "aws_route_table_association" "sous_reseau" {
  for_each = local.sous_reseaux

  subnet_id      = aws_subnet.sous_reseau[each.key].id
  route_table_id = aws_route_table.niveau[each.value.niveau].id
}

# --- Points de terminaison VPC -------------------------------------------------------------

resource "aws_security_group" "points_de_terminaison" {
  name        = "${var.prefixe}-points-de-terminaison"
  description = "HTTPS depuis le VPC vers les points de terminaison d'interface"
  vpc_id      = aws_vpc.vpc.id

  tags = {
    Name = "${var.prefixe}-points-de-terminaison"
  }
}

resource "aws_vpc_security_group_ingress_rule" "points_de_terminaison_https" {
  security_group_id = aws_security_group.points_de_terminaison.id
  description       = "HTTPS depuis le VPC"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  cidr_ipv4         = aws_vpc.vpc.cidr_block
}

resource "aws_vpc_endpoint" "interface" {
  for_each = var.services_points_de_terminaison

  vpc_id              = aws_vpc.vpc.id
  service_name        = "com.amazonaws.${var.region}.${each.key}"
  vpc_endpoint_type   = "Interface"
  private_dns_enabled = true
  subnet_ids          = [for k, s in local.sous_reseaux : aws_subnet.sous_reseau[k].id if s.niveau == "applications"]
  security_group_ids  = [aws_security_group.points_de_terminaison.id]

  tags = {
    Name = "${var.prefixe}-${each.key}"
  }
}

# Passerelle S3 limitée aux compartiments du compte, plus celui des couches d'images ECR
# (documentation ECR : les couches sont servies depuis S3).
resource "aws_vpc_endpoint" "s3" {
  vpc_id            = aws_vpc.vpc.id
  service_name      = "com.amazonaws.${var.region}.s3"
  vpc_endpoint_type = "Gateway"
  route_table_ids   = [for t in aws_route_table.niveau : t.id]

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "CompartimentsDuCompte"
        Effect    = "Allow"
        Principal = "*"
        Action    = "s3:*"
        Resource  = "*"
        Condition = { StringEquals = { "aws:ResourceAccount" = var.compte_aws } }
      },
      {
        Sid       = "CouchesEcr"
        Effect    = "Allow"
        Principal = "*"
        Action    = "s3:GetObject"
        Resource  = "arn:aws:s3:::prod-${var.region}-starport-layer-bucket/*"
      },
    ]
  })

  tags = {
    Name = "${var.prefixe}-s3"
  }
}

# --- Journaux de flux VPC ------------------------------------------------------------------

resource "aws_cloudwatch_log_group" "journaux_de_flux" {
  name              = "/${var.prefixe}/vpc/journaux-de-flux"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

resource "aws_iam_role" "journaux_de_flux" {
  name                 = "${var.prefixe}-journaux-de-flux"
  path                 = "/ledger/"
  permissions_boundary = var.frontiere_permissions_arn

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "vpc-flow-logs.amazonaws.com" }
      Action    = "sts:AssumeRole"
      Condition = {
        StringEquals = { "aws:SourceAccount" = var.compte_aws }
        ArnLike      = { "aws:SourceArn" = "arn:aws:ec2:${var.region}:${var.compte_aws}:vpc-flow-log/*" }
      }
    }]
  })
}

resource "aws_iam_role_policy" "journaux_de_flux" {
  name = "ecriture-journaux-de-flux"
  role = aws_iam_role.journaux_de_flux.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["logs:CreateLogStream", "logs:PutLogEvents", "logs:DescribeLogStreams"]
      Resource = ["${aws_cloudwatch_log_group.journaux_de_flux.arn}:*"]
    }]
  })
}

resource "aws_flow_log" "vpc" {
  vpc_id                   = aws_vpc.vpc.id
  traffic_type             = "ALL"
  log_destination_type     = "cloud-watch-logs"
  log_destination          = aws_cloudwatch_log_group.journaux_de_flux.arn
  iam_role_arn             = aws_iam_role.journaux_de_flux.arn
  max_aggregation_interval = 60

  tags = {
    Name = "${var.prefixe}-vpc"
  }
}
