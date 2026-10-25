# Grappe Aurora PostgreSQL du journal comptable (source de vérité, ADR 0007). Tout est chiffré
# par des CMK (CLAUDE.md, « Sécurité ») : stockage et Performance Insights par « aurora », secret
# du mot de passe maître par « secrets », journaux exportés par « logs ». TLS exigé
# (rds.force_ssl), en TLS 1.3 au minimum : Aurora 18, Npgsql (.NET 10, OpenSSL 3) et Debezium
# (Java 21) le prennent tous en charge. Réplication logique active pour Debezium (ADR 0020),
# aucune exposition publique.

locals {
  famille_parametres = "aurora-postgresql${split(".", var.version_moteur)[0]}"
  groupe_journaux    = "/aws/rds/cluster/${var.prefixe}/postgresql"

  # Paramètres statiques appliqués au redémarrage ; à la création, la grappe démarre avec eux.
  parametres_grappe = {
    "rds.force_ssl"              = { valeur = "1", application = "pending-reboot" }
    "ssl_min_protocol_version"   = { valeur = "TLSv1.3", application = "pending-reboot" }
    "rds.logical_replication"    = { valeur = "1", application = "pending-reboot" }
    "log_connections"            = { valeur = "all", application = "immediate" } # PostgreSQL 18 : liste d'aspects, plus un booléen
    "log_disconnections"         = { valeur = "1", application = "immediate" }
    "log_statement"              = { valeur = "ddl", application = "immediate" }
    "log_min_duration_statement" = { valeur = "1000", application = "immediate" }
  }

  serverless = var.capacite_serverless != null

  # Ordre de promotion : rédacteur au rang 0. En Serverless v2, les lecteurs commencent au rang 2,
  # le seul où ils se mettent en pause indépendamment du rédacteur (documentation AWS) ; un lecteur
  # reste la cible de basculement quel que soit son rang.
  instances = { for i in range(var.nombre_instances) : tostring(i + 1) => (i == 0 ? 0 : (local.serverless ? i + 1 : i)) }
}

resource "aws_db_subnet_group" "donnees" {
  name        = var.prefixe
  description = "Sous-reseaux donnees (Aurora)"
  subnet_ids  = var.sous_reseaux
}

# Aucune règle ici : chaque client ajoute sa propre règle d'entrée (port 5432 depuis son groupe).
# Aucune règle de sortie : Aurora n'initie aucune connexion.
resource "aws_security_group" "aurora" {
  name        = "${var.prefixe}-aurora"
  description = "Aurora PostgreSQL : entrees ajoutees par chaque client"
  vpc_id      = var.vpc_id

  tags = {
    Name = "${var.prefixe}-aurora"
  }
}

resource "aws_rds_cluster_parameter_group" "grappe" {
  name        = "${var.prefixe}-${local.famille_parametres}"
  family      = local.famille_parametres
  description = "TLS 1.3 exige, replication logique, journalisation des connexions et du DDL"

  dynamic "parameter" {
    for_each = local.parametres_grappe
    content {
      name         = parameter.key
      value        = parameter.value.valeur
      apply_method = parameter.value.application
    }
  }

  lifecycle {
    create_before_destroy = true
  }
}

# Créé avant la grappe : sinon RDS le crée lui-même, sans CMK ni rétention.
resource "aws_cloudwatch_log_group" "postgresql" {
  name              = local.groupe_journaux
  retention_in_days = var.retention_journaux_jours
  kms_key_id        = var.cle_kms_journaux_arn
}

resource "aws_rds_cluster" "grappe" {
  #checkov:skip=CKV2_AWS_8:Sauvegardes natives Aurora avec PITR de 35 jours ; AWS Backup et copie interrégionale relèvent de la reprise après sinistre (phase 11).
  #checkov:skip=CKV2_AWS_27:log_statement = ddl et requêtes lentes ; « all » écrirait dans les journaux les valeurs des écritures comptables et le texte des ALTER ROLE … PASSWORD.
  cluster_identifier = var.prefixe
  engine             = "aurora-postgresql"
  engine_mode        = "provisioned"
  engine_version     = var.version_moteur
  database_name      = var.nom_base
  port               = 5432

  # Mot de passe géré par RDS dans Secrets Manager (rotation automatique tous les 7 jours).
  master_username               = var.utilisateur_maitre
  manage_master_user_password   = true
  master_user_secret_kms_key_id = var.cle_kms_secrets_arn

  storage_encrypted = true
  kms_key_id        = var.cle_kms_aurora_arn

  db_subnet_group_name            = aws_db_subnet_group.donnees.name
  vpc_security_group_ids          = [aws_security_group.aurora.id]
  db_cluster_parameter_group_name = aws_rds_cluster_parameter_group.grappe.name

  iam_database_authentication_enabled = true
  enabled_cloudwatch_logs_exports     = ["postgresql"]

  backup_retention_period      = var.retention_sauvegardes_jours
  preferred_backup_window      = "06:00-07:00"         # UTC : 1 h-2 h, heure de l'Est
  preferred_maintenance_window = "sun:07:30-sun:08:30" # UTC, après la sauvegarde
  copy_tags_to_snapshot        = true

  # Désactivables pour un environnement jetable seulement (dev, ADR 0027).
  deletion_protection       = var.protection_suppression
  skip_final_snapshot       = !var.instantane_final
  final_snapshot_identifier = var.instantane_final ? "${var.prefixe}-final" : null

  # Serverless v2 (instances db.serverless) : avec la réplication logique active, le rédacteur ne
  # se met jamais en pause et reste à sa plus petite capacité active (ADR 0027).
  dynamic "serverlessv2_scaling_configuration" {
    for_each = local.serverless ? [var.capacite_serverless] : []
    content {
      min_capacity             = serverlessv2_scaling_configuration.value.minimum_acu
      max_capacity             = serverlessv2_scaling_configuration.value.maximum_acu
      seconds_until_auto_pause = serverlessv2_scaling_configuration.value.minimum_acu == 0 ? serverlessv2_scaling_configuration.value.secondes_avant_pause : null
    }
  }

  allow_major_version_upgrade = false
  apply_immediately           = false

  depends_on = [aws_cloudwatch_log_group.postgresql]
}

resource "aws_rds_cluster_instance" "instance" {
  #checkov:skip=CKV_AWS_118:Surveillance améliorée non retenue : Performance Insights (CMK) et métriques CloudWatch suffisent ; elle écrirait dans le groupe RDSOSMetrics partagé, hors CMK du projet.
  #checkov:skip=CKV_AWS_226:Montées de version mineures par PR (version_moteur), testées en dev avant prod, jamais pendant une fenêtre imprévue.
  for_each = local.instances

  identifier         = "${var.prefixe}-${each.key}"
  cluster_identifier = aws_rds_cluster.grappe.id
  engine             = aws_rds_cluster.grappe.engine
  engine_version     = aws_rds_cluster.grappe.engine_version
  instance_class     = local.serverless ? "db.serverless" : var.classe_instance

  db_subnet_group_name = aws_db_subnet_group.donnees.name
  publicly_accessible  = false
  ca_cert_identifier   = "rds-ca-rsa2048-g1"
  promotion_tier       = each.value

  performance_insights_enabled          = true
  performance_insights_kms_key_id       = var.cle_kms_aurora_arn
  performance_insights_retention_period = 7

  auto_minor_version_upgrade = false
  copy_tags_to_snapshot      = true
  apply_immediately          = false
}

# --- Alarmes ---------------------------------------------------------------------------------

locals {
  # Métriques par instance : le rédacteur change au basculement, chaque instance est surveillée.
  alarmes_par_instance = {
    stockage-local = {
      metrique    = "FreeLocalStorage"
      comparaison = "LessThanThreshold"
      seuil       = var.seuils_alarmes.stockage_local_libre
      statistique = "Minimum"
      periodes    = 2
      description = "Stockage local libre faible (tables temporaires, tris)"
    }
    connexions = {
      metrique    = "DatabaseConnections"
      comparaison = "GreaterThanThreshold"
      seuil       = var.seuils_alarmes.connexions
      statistique = "Maximum"
      periodes    = 2
      description = "Nombre de connexions élevé"
    }
    retard-slot = {
      metrique    = "OldestReplicationSlotLag"
      comparaison = "GreaterThanThreshold"
      seuil       = var.seuils_alarmes.retard_slot_octets
      statistique = "Maximum"
      periodes    = var.seuils_alarmes.periodes_retard_slot_alerte
      description = "Retard du slot de réplication logique (Debezium arrêté ou en retard : le WAL s'accumule)"
    }
    disque-slots = {
      metrique    = "ReplicationSlotDiskUsage"
      comparaison = "GreaterThanThreshold"
      seuil       = var.seuils_alarmes.disque_slots_octets
      statistique = "Maximum"
      periodes    = var.seuils_alarmes.periodes_retard_slot_alerte
      description = "Espace disque des fichiers de slots de réplication"
    }
  }

  alarmes_instances = merge([
    for instance, _ in local.instances : {
      # FreeLocalStorage n'est pas publiée pour les instances Serverless v2.
      for nom, a in local.alarmes_par_instance : "${instance}-${nom}" => merge(a, { instance = instance, nom = nom })
      if !(local.serverless && nom == "stockage-local")
    }
  ]...)
}

resource "aws_cloudwatch_metric_alarm" "volume" {
  alarm_name          = "${var.prefixe}-aurora-volume"
  alarm_description   = "Volume de stockage Aurora utilisé au-delà du seuil"
  namespace           = "AWS/RDS"
  metric_name         = "VolumeBytesUsed"
  dimensions          = { DBClusterIdentifier = aws_rds_cluster.grappe.cluster_identifier }
  statistic           = "Maximum"
  period              = 3600
  evaluation_periods  = 1
  comparison_operator = "GreaterThanThreshold"
  threshold           = var.seuils_alarmes.volume_utilise_octets
  treat_missing_data  = "missing"
  alarm_actions       = var.actions_alarmes
  ok_actions          = var.actions_alarmes
}

resource "aws_cloudwatch_metric_alarm" "instance" {
  for_each = local.alarmes_instances

  alarm_name          = "${var.prefixe}-${each.value.instance}-${each.value.nom}"
  alarm_description   = "${each.value.description} (instance ${var.prefixe}-${each.value.instance})"
  namespace           = "AWS/RDS"
  metric_name         = each.value.metrique
  dimensions          = { DBInstanceIdentifier = aws_rds_cluster_instance.instance[each.value.instance].identifier }
  statistic           = each.value.statistique
  period              = 300
  evaluation_periods  = each.value.periodes
  comparison_operator = each.value.comparaison
  threshold           = each.value.seuil
  # Une instance qui ne publie pas la métrique (lecteur sans slot) n'est pas en alarme.
  treat_missing_data = "notBreaching"
  alarm_actions      = var.actions_alarmes
  ok_actions         = var.actions_alarmes
}
