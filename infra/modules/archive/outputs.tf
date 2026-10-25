output "nom_bucket" {
  description = "Nom du bucket d'archive."
  value       = aws_s3_bucket.archive.id
}

output "arn_bucket" {
  description = "ARN du bucket d'archive."
  value       = aws_s3_bucket.archive.arn
}
