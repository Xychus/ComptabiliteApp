"""Vérifications du chiffrement par CMK (CLAUDE.md, « Sécurité ») : jamais de clé aws/*.

CKV_LEDGER_1 : aucune ressource ni source de données ne désigne une clé gérée par AWS.
CKV_LEDGER_2 : toute ressource chiffrable désigne explicitement une CMK ; sans elle, AWS
               choisirait sa propre clé (aws/s3, aws/secretsmanager, aws/ssm…).
"""

import re

from checkov.common.models.enums import CheckCategories, CheckResult
from checkov.terraform.checks.data.base_check import BaseDataCheck
from checkov.terraform.checks.resource.base_resource_check import BaseResourceCheck

CLE_GEREE_PAR_AWS = re.compile(r"alias/aws/")


def _contient_cle_geree_par_aws(valeur):
    if isinstance(valeur, str):
        return bool(CLE_GEREE_PAR_AWS.search(valeur))
    if isinstance(valeur, dict):
        return any(_contient_cle_geree_par_aws(v) for k, v in valeur.items() if not str(k).startswith("__"))
    if isinstance(valeur, list):
        return any(_contient_cle_geree_par_aws(v) for v in valeur)
    return False


class CleGereeParAwsDansRessource(BaseResourceCheck):
    def __init__(self):
        super().__init__(
            name="Aucune ressource ne désigne une clé KMS gérée par AWS (alias/aws/*)",
            id="CKV_LEDGER_1",
            categories=[CheckCategories.ENCRYPTION],
            supported_resources=["aws_*"],
        )

    def scan_resource_conf(self, conf):
        return CheckResult.FAILED if _contient_cle_geree_par_aws(conf) else CheckResult.PASSED


class CleGereeParAwsDansDonnees(BaseDataCheck):
    def __init__(self):
        super().__init__(
            name="Aucune source de données ne désigne une clé KMS gérée par AWS (alias/aws/*)",
            id="CKV_LEDGER_1",
            categories=[CheckCategories.ENCRYPTION],
            supported_data=["aws_*"],
        )

    def scan_data_conf(self, conf):
        return CheckResult.FAILED if _contient_cle_geree_par_aws(conf) else CheckResult.PASSED


def _valeurs(conf, chemin):
    """Valeurs au bout d'un chemin « bloc.bloc.attribut », en traversant les listes HCL."""
    courantes = [conf]
    for segment in chemin.split("."):
        suivantes = []
        for c in courantes:
            if isinstance(c, list):
                c = c[0] if c else None
            if isinstance(c, dict) and segment in c:
                suivantes.append(c[segment])
        courantes = suivantes
    resultat = []
    for v in courantes:
        resultat.extend(v if isinstance(v, list) else [v])
    return [v for v in resultat if v not in (None, "", [])]


def _premiere(conf, attribut):
    valeurs = _valeurs(conf, attribut)
    return valeurs[0] if valeurs else None


# Type de ressource → (chemin de l'attribut CMK, condition d'application facultative).
ATTRIBUTS_CMK = {
    "aws_cloudwatch_log_group": ("kms_key_id", None),
    "aws_ebs_volume": ("kms_key_id", None),
    "aws_ecr_repository": ("encryption_configuration.kms_key", None),
    "aws_ecs_cluster": ("configuration.managed_storage_configuration.fargate_ephemeral_storage_kms_key_id", None),
    "aws_lambda_function": ("kms_key_arn", None),
    "aws_msk_cluster": ("encryption_info.encryption_at_rest_kms_key_arn", None),
    "aws_rds_cluster": ("kms_key_id", None),
    "aws_s3_bucket_server_side_encryption_configuration": (
        "rule.apply_server_side_encryption_by_default.kms_master_key_id",
        None,
    ),
    "aws_secretsmanager_secret": ("kms_key_id", None),
    "aws_sns_topic": ("kms_master_key_id", None),
    "aws_sqs_queue": ("kms_master_key_id", None),
    "aws_ssm_parameter": ("key_id", lambda conf: _premiere(conf, "type") == "SecureString"),
}


class CmkExplicite(BaseResourceCheck):
    def __init__(self):
        super().__init__(
            name="Les ressources chiffrables désignent explicitement une CMK gérée par le client",
            id="CKV_LEDGER_2",
            categories=[CheckCategories.ENCRYPTION],
            supported_resources=list(ATTRIBUTS_CMK),
        )

    def scan_resource_conf(self, conf):
        chemin, condition = ATTRIBUTS_CMK[self.entity_type]
        if condition is not None and not condition(conf):
            return CheckResult.PASSED
        if self.entity_type == "aws_ecr_repository" and _premiere(conf, "encryption_configuration.encryption_type") != "KMS":
            return CheckResult.FAILED
        return CheckResult.PASSED if _valeurs(conf, chemin) else CheckResult.FAILED


check_ressource = CleGereeParAwsDansRessource()
check_donnees = CleGereeParAwsDansDonnees()
check_cmk = CmkExplicite()
