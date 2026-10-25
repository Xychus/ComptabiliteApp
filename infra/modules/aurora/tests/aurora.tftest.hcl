# Fournisseur simulé : aucun appel AWS, aucun identifiant nécessaire.
mock_provider "aws" {
  mock_resource "aws_rds_cluster" {
    defaults = {
      master_user_secret = [{
        kms_key_id    = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-4444-4a5b-9c3d-0123456789ab"
        secret_arn    = "arn:aws:secretsmanager:ca-central-1:111122223333:secret:rds!cluster-abc-123456"
        secret_status = "active"
      }]
    }
  }
}

variables {
  prefixe              = "ledger-dev"
  vpc_id               = "vpc-0123456789abcdef0"
  sous_reseaux         = ["subnet-0000000000000000a", "subnet-0000000000000000b", "subnet-0000000000000000c"]
  cle_kms_aurora_arn   = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-3333-4a5b-9c3d-0123456789ab"
  cle_kms_secrets_arn  = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-4444-4a5b-9c3d-0123456789ab"
  cle_kms_journaux_arn = "arn:aws:kms:ca-central-1:111122223333:key/0b6f8a1e-1111-4a5b-9c3d-0123456789ab"
}

run "tout_est_chiffre_par_cmk" {
  command = apply

  assert {
    condition     = aws_rds_cluster.grappe.storage_encrypted && aws_rds_cluster.grappe.kms_key_id == var.cle_kms_aurora_arn
    error_message = "Le stockage Aurora doit être chiffré par la CMK « aurora »."
  }

  assert {
    condition     = aws_rds_cluster.grappe.manage_master_user_password && aws_rds_cluster.grappe.master_user_secret_kms_key_id == var.cle_kms_secrets_arn
    error_message = "Le mot de passe maître doit être géré dans Secrets Manager et chiffré par la CMK « secrets »."
  }

  assert {
    condition     = alltrue([for i in aws_rds_cluster_instance.instance : i.performance_insights_enabled && i.performance_insights_kms_key_id == var.cle_kms_aurora_arn])
    error_message = "Performance Insights doit être actif et chiffré par la CMK « aurora » sur chaque instance."
  }

  assert {
    condition     = aws_cloudwatch_log_group.postgresql.name == "/aws/rds/cluster/ledger-dev/postgresql" && aws_cloudwatch_log_group.postgresql.kms_key_id == var.cle_kms_journaux_arn
    error_message = "Le groupe des journaux exportés doit porter le nom attendu par RDS et la CMK « logs »."
  }

  assert {
    condition     = aws_rds_cluster.grappe.enabled_cloudwatch_logs_exports == toset(["postgresql"])
    error_message = "Les journaux PostgreSQL doivent être exportés."
  }
}

run "tls_exige_et_replication_logique" {
  command = apply

  assert {
    condition     = aws_rds_cluster_parameter_group.grappe.family == "aurora-postgresql18"
    error_message = "La famille de paramètres doit suivre la version majeure 18."
  }

  assert {
    condition     = one([for p in aws_rds_cluster_parameter_group.grappe.parameter : p.value if p.name == "rds.force_ssl"]) == "1"
    error_message = "rds.force_ssl = 1 est exigé : toute connexion sans TLS est refusée."
  }

  assert {
    condition     = one([for p in aws_rds_cluster_parameter_group.grappe.parameter : p.value if p.name == "ssl_min_protocol_version"]) == "TLSv1.3"
    error_message = "TLS 1.3 au minimum est exigé : le serveur et tous ses clients le prennent en charge."
  }

  assert {
    condition     = one([for p in aws_rds_cluster_parameter_group.grappe.parameter : p.value if p.name == "rds.logical_replication"]) == "1"
    error_message = "La réplication logique (wal_level = logical) est exigée par Debezium."
  }

  # PostgreSQL 18 : log_connections est une liste d'aspects ; Aurora refuse l'ancienne valeur booléenne « 1 ».
  assert {
    condition     = one([for p in aws_rds_cluster_parameter_group.grappe.parameter : p.value if p.name == "log_connections"]) == "all"
    error_message = "log_connections doit valoir « all » (réception, authentification, autorisation, durées)."
  }
}

run "prive_protege_et_sauvegarde" {
  command = apply

  assert {
    condition     = alltrue([for i in aws_rds_cluster_instance.instance : !i.publicly_accessible])
    error_message = "Aucune instance ne doit être accessible publiquement."
  }

  assert {
    condition     = aws_rds_cluster.grappe.deletion_protection && !aws_rds_cluster.grappe.skip_final_snapshot
    error_message = "Protection contre la suppression et instantané final exigés."
  }

  assert {
    condition     = aws_rds_cluster.grappe.backup_retention_period == 35
    error_message = "Sauvegardes conservées 35 jours (PITR) par défaut."
  }

  assert {
    condition     = length(aws_security_group.aurora.ingress) == 0 && length(aws_security_group.aurora.egress) == 0
    error_message = "Le groupe d'Aurora ne s'ouvre que par les règles des clients."
  }

  assert {
    condition     = length(aws_rds_cluster_instance.instance) == 2 && length(distinct([for i in aws_rds_cluster_instance.instance : i.promotion_tier])) == 2
    error_message = "Un rédacteur et un lecteur, avec un ordre de promotion explicite."
  }
}

run "alarmes" {
  command = apply

  assert {
    condition     = aws_cloudwatch_metric_alarm.volume.metric_name == "VolumeBytesUsed"
    error_message = "Une alarme sur le volume de stockage est attendue."
  }

  # Quatre alarmes par instance : stockage local, connexions, retard et disque des slots.
  assert {
    condition     = length(aws_cloudwatch_metric_alarm.instance) == 8 && contains([for a in aws_cloudwatch_metric_alarm.instance : a.metric_name], "OldestReplicationSlotLag")
    error_message = "Les alarmes de stockage, de connexions et de retard du slot logique sont attendues sur chaque instance."
  }
}

run "provisionne_et_protege_par_defaut" {
  command = apply

  assert {
    condition     = alltrue([for i in aws_rds_cluster_instance.instance : i.instance_class == "db.r7g.large"]) && length(aws_rds_cluster.grappe.serverlessv2_scaling_configuration) == 0
    error_message = "Sans capacité Serverless, les instances sont provisionnées."
  }

  assert {
    condition     = aws_rds_cluster.grappe.final_snapshot_identifier == "ledger-dev-final"
    error_message = "L'instantané final est nommé d'après la grappe."
  }
}

run "serverless_jetable" {
  command = apply

  variables {
    capacite_serverless    = { minimum_acu = 0, maximum_acu = 4, secondes_avant_pause = 900 }
    protection_suppression = false
    instantane_final       = false
  }

  assert {
    condition     = alltrue([for i in aws_rds_cluster_instance.instance : i.instance_class == "db.serverless"])
    error_message = "Les instances doivent être de classe db.serverless."
  }

  assert {
    condition = (aws_rds_cluster.grappe.serverlessv2_scaling_configuration[0].min_capacity == 0
      && aws_rds_cluster.grappe.serverlessv2_scaling_configuration[0].max_capacity == 4
    && aws_rds_cluster.grappe.serverlessv2_scaling_configuration[0].seconds_until_auto_pause == 900)
    error_message = "Capacité de 0 à 4 ACU, pause après 15 minutes."
  }

  # Le lecteur au rang 2 se met en pause indépendamment du rédacteur.
  assert {
    condition     = aws_rds_cluster_instance.instance["1"].promotion_tier == 0 && aws_rds_cluster_instance.instance["2"].promotion_tier == 2
    error_message = "Rédacteur au rang 0, lecteur au rang 2."
  }

  assert {
    condition     = !aws_rds_cluster.grappe.deletion_protection && aws_rds_cluster.grappe.skip_final_snapshot
    error_message = "Environnement jetable : ni protection contre la suppression, ni instantané final."
  }

  # Le chiffrement et le TLS ne changent pas en Serverless.
  assert {
    condition     = aws_rds_cluster.grappe.kms_key_id == var.cle_kms_aurora_arn && alltrue([for i in aws_rds_cluster_instance.instance : i.performance_insights_kms_key_id == var.cle_kms_aurora_arn])
    error_message = "Le chiffrement par la CMK « aurora » s'applique aussi en Serverless."
  }

  assert {
    condition     = length(aws_cloudwatch_metric_alarm.instance) == 6 && !contains([for a in aws_cloudwatch_metric_alarm.instance : a.metric_name], "FreeLocalStorage")
    error_message = "FreeLocalStorage n'existe pas en Serverless : trois alarmes par instance."
  }
}

run "refuse_une_capacite_invalide" {
  command = plan

  variables {
    capacite_serverless = { minimum_acu = 0, maximum_acu = 4, secondes_avant_pause = 60 }
  }

  expect_failures = [var.capacite_serverless]
}

run "refuse_une_cle_geree_par_aws" {
  command = plan

  variables {
    cle_kms_aurora_arn = "arn:aws:kms:ca-central-1:111122223333:alias/aws/rds"
  }

  expect_failures = [var.cle_kms_aurora_arn]
}

run "refuse_une_instance_unique" {
  command = plan

  variables {
    nombre_instances = 1
  }

  expect_failures = [var.nombre_instances]
}

run "refuse_une_autre_version_majeure" {
  command = plan

  variables {
    version_moteur = "17.5"
  }

  expect_failures = [var.version_moteur]
}
