# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {}

variables {
  prefixe                 = "ledger-dev"
  region                  = "ca-central-1"
  cle_kms_xray_arn        = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-8888-4a5b-9c3d-0123456789ab"
  cle_kms_journaux_arn    = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-1111-4a5b-9c3d-0123456789ab"
  suffixe_equilibreur     = "app/ledger-dev-api/abc"
  suffixes_groupes_cibles = { command-api = "targetgroup/ledger-dev-command-api/a", query-api = "targetgroup/ledger-dev-query-api/b" }
  instances_aurora        = ["ledger-dev-1", "ledger-dev-2"]
}

run "traces_et_metriques_chiffrees_par_cmk" {
  command = apply

  assert {
    condition     = aws_xray_encryption_config.traces.type == "KMS" && aws_xray_encryption_config.traces.key_id == var.cle_kms_xray_arn
    error_message = "Les traces X-Ray sont chiffrées par la CMK « xray »."
  }

  assert {
    condition     = aws_cloudwatch_log_group.metriques.kms_key_id == var.cle_kms_journaux_arn
    error_message = "Les métriques EMF sont dans un groupe chiffré par la CMK « logs »."
  }
}

run "tableau_de_bord_du_plan" {
  command = apply

  assert {
    condition = toset([for w in jsondecode(aws_cloudwatch_dashboard.exploitation.dashboard_body).widgets : w.properties.title]) == toset([
      "Latence des API (équilibreur, p50 / p95 / p99)",
      "Retard des projections (messages non consommés)",
      "Taux de 409 (API de commandes)",
      "Taux de 422 (API de commandes)",
      "Retard CDC (slot logique de Debezium, octets)",
      "Kafka : nœuds présents, partitions sous-répliquées",
      "Échéance des certificats (jours restants)",
    ])
    error_message = "Le tableau de bord porte latence, 409/422, retard des projections et retard CDC."
  }
}
