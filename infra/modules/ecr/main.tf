# Un dépôt par service : étiquettes immuables (la version SemVer exacte, jamais « latest »),
# chiffrement par la CMK « ecr », analyse à la poussée. Les images étiquetées ne sont jamais
# expirées : une version déployée doit rester récupérable. Un dépôt contenant des images n'est
# supprimé que si suppression_forcee est vrai (environnement jetable, ADR 0027).

resource "aws_ecr_repository" "depot" {
  for_each = var.depots

  name                 = "${var.prefixe}/${each.key}"
  image_tag_mutability = "IMMUTABLE"
  force_delete         = var.suppression_forcee

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

# Lambda tire l'image avec son propre principal de service : la politique du dépôt doit l'autoriser,
# pour les seules fonctions du compte (documentation Lambda, images de conteneur).
resource "aws_ecr_repository_policy" "lambda" {
  for_each = var.depots_lambda

  repository = aws_ecr_repository.depot[each.key].name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "TirageParLambda"
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = ["ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer"]
      Condition = { ArnLike = { "aws:sourceArn" = var.arn_fonctions_lambda } }
    }]
  })

  lifecycle {
    precondition {
      condition     = var.arn_fonctions_lambda != null
      error_message = "arn_fonctions_lambda est exigé avec depots_lambda."
    }
  }
}
