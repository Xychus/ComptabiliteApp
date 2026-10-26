# Observabilité (phase 8) : traces X-Ray chiffrées par la CMK « xray » (réglage du compte et de la
# région), groupe de journaux des métriques EMF des collecteurs ADOT (CMK « logs »), tableau de
# bord : latence des API, taux de 409/422, retard des projections, retard CDC, état de Kafka et
# échéance des certificats.

resource "aws_xray_encryption_config" "traces" {
  type   = "KMS"
  key_id = var.cle_kms_xray_arn
}

resource "aws_cloudwatch_log_group" "metriques" {
  name              = "/${var.prefixe}/metriques-api"
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

locals {
  taux_metier = [
    for code in ["409", "422"] : {
      type   = "metric"
      width  = 12
      height = 6
      properties = {
        title  = "Taux de ${code} (API de commandes)"
        region = var.region
        view   = "timeSeries"
        period = 300
        yAxis  = { left = { min = 0, label = "%" } }
        metrics = [
          ["Ledger/Api", "http.server.request.duration", "service.name", "command-api", "http.response.status_code", code, { id = "erreurs", stat = "SampleCount", visible = false }],
          ["Ledger/Api", "http.server.request.duration", "service.name", "command-api", { id = "total", stat = "SampleCount", visible = false }],
          [{ expression = "100 * erreurs / total", label = "${code} / requêtes (%)", id = "taux" }],
        ]
      }
    }
  ]

  tableau = {
    widgets = concat(
      [
        {
          type   = "metric"
          width  = 12
          height = 6
          properties = {
            title  = "Latence des API (équilibreur, p50 / p95 / p99)"
            region = var.region
            view   = "timeSeries"
            period = 60
            metrics = flatten([
              for api, suffixe in var.suffixes_groupes_cibles : [
                for p in ["p50", "p95", "p99"] : ["AWS/ApplicationELB", "TargetResponseTime", "LoadBalancer", var.suffixe_equilibreur, "TargetGroup", suffixe, { stat = p, label = "${api} ${p}" }]
              ]
            ])
          }
        },
        {
          type   = "metric"
          width  = 12
          height = 6
          properties = {
            title   = "Retard des projections (messages non consommés)"
            region  = var.region
            view    = "timeSeries"
            period  = 60
            metrics = [["Ledger/Kafka", "RetardGroupe", "Groupe", "ledger-projections", { stat = "Maximum" }]]
          }
        },
      ],
      local.taux_metier,
      [
        {
          type   = "metric"
          width  = 12
          height = 6
          properties = {
            title   = "Retard CDC (slot logique de Debezium, octets)"
            region  = var.region
            view    = "timeSeries"
            period  = 60
            metrics = [for i in var.instances_aurora : ["AWS/RDS", "OldestReplicationSlotLag", "DBInstanceIdentifier", i, { stat = "Maximum" }]]
          }
        },
        {
          type   = "metric"
          width  = 12
          height = 6
          properties = {
            title  = "Kafka : nœuds présents, partitions sous-répliquées"
            region = var.region
            view   = "timeSeries"
            period = 60
            metrics = concat(
              [for n in ["kafka-1", "kafka-2", "kafka-3"] : ["Ledger/Kafka", "NoeudsPresents", "Noeud", n, { stat = "Minimum" }]],
              [for n in ["kafka-1", "kafka-2", "kafka-3"] : ["Ledger/Kafka", "PartitionsSousRepliquees", "Noeud", n, { stat = "Maximum" }]],
            )
          }
        },
        {
          type   = "metric"
          width  = 12
          height = 6
          properties = {
            title  = "Échéance des certificats (jours restants)"
            region = var.region
            view   = "timeSeries"
            period = 86400
            metrics = [
              ["Ledger/Pki", "JoursAvantEcheance", "Identite", "minimum-certificats-finaux", { stat = "Minimum" }],
              ["Ledger/Pki", "JoursAvantEcheance", "Identite", "ac-intermediaire", { stat = "Minimum" }],
            ]
          }
        },
      ],
    )
  }
}

resource "aws_cloudwatch_dashboard" "exploitation" {
  dashboard_name = "${var.prefixe}-exploitation"
  dashboard_body = jsonencode(local.tableau)
}
