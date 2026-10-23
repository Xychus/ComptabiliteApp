# Un dépôt par service : étiquettes immuables (la version SemVer exacte, jamais « latest »),
# chiffrement par la CMK « ecr », analyse à la poussée. Les images étiquetées ne sont jamais
# expirées : une version déployée doit rester récupérable.

resource "aws_ecr_repository" "depot" {
  for_each = var.depots

  name                 = "${var.prefixe}/${each.key}"
  image_tag_mutability = "IMMUTABLE"
  force_delete         = false

  encryption_configuration {
    encryption_type = "KMS"
    kms_key         = var.cle_kms_arn
  }

  image_scanning_configuration {
    scan_on_push = true
  }

  tags = {
    service = each.key
  }
}

resource "aws_ecr_lifecycle_policy" "depot" {
  for_each = var.depots

  repository = aws_ecr_repository.depot[each.key].name

  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "Expirer les images sans étiquette"
      selection = {
        tagStatus   = "untagged"
        countType   = "sinceImagePushed"
        countUnit   = "days"
        countNumber = var.jours_conservation_sans_etiquette
      }
      action = { type = "expire" }
    }]
  })
}
