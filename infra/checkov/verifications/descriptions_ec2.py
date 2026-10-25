"""Vérification des descriptions EC2 (CKV_LEDGER_3).

EC2 refuse à l'apply, et seulement à l'apply (ni validate ni plan ne le voient), une description de
groupe de sécurité ou de règle hors de l'ensemble « a-zA-Z0-9. _-:/()#,@[]+=&;{}!$* » : ni accent,
ni apostrophe, ni guillemet français. Premier apply de dev interrompu le 2026-10-25 pour cette raison.
"""

import re

from checkov.common.models.enums import CheckCategories, CheckResult
from checkov.terraform.checks.resource.base_resource_check import BaseResourceCheck

DESCRIPTION_VALIDE = re.compile(r"^[a-zA-Z0-9. _\-:/()#,@\[\]+=&;{}!$*]{0,255}$")
INTERPOLATION = re.compile(r"\$\{[^}]*\}")


def _descriptions(conf):
    """Descriptions de la ressource et de ses blocs ingress / egress en ligne."""
    valeurs = list(conf.get("description", []))
    for bloc in ("ingress", "egress"):
        for regle in conf.get(bloc, []):
            if isinstance(regle, dict):
                valeurs.extend(regle.get("description", []))
            elif isinstance(regle, list):
                for r in regle:
                    if isinstance(r, dict):
                        valeurs.extend(r.get("description", []))
    return [v for v in valeurs if isinstance(v, str)]


class DescriptionEc2Valide(BaseResourceCheck):
    def __init__(self):
        super().__init__(
            name="Les descriptions de groupes de sécurité et de règles n'utilisent que les caractères acceptés par EC2",
            id="CKV_LEDGER_3",
            categories=[CheckCategories.NETWORKING],
            supported_resources=[
                "aws_security_group",
                "aws_vpc_security_group_ingress_rule",
                "aws_vpc_security_group_egress_rule",
                "aws_security_group_rule",
            ],
        )

    def scan_resource_conf(self, conf):
        for description in _descriptions(conf):
            # Les interpolations (${...}) sont vérifiées après résolution par Checkov ; leur texte brut est ignoré.
            if not DESCRIPTION_VALIDE.match(INTERPOLATION.sub("x", description)):
                return CheckResult.FAILED
        return CheckResult.PASSED


check = DescriptionEc2Valide()
