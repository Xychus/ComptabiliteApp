"""Enregistre (ou met à jour) le connecteur d'outbox auprès de Kafka Connect.

Usage : enregistrer_connecteur.py <url Kafka Connect> <nom> <ledger-outbox.json> <paramètres.json>

La configuration invariante (ledger-outbox.json) et les paramètres de l'environnement sont
fusionnés ; un paramètre ne peut pas redéfinir une clé invariante (ADR 0020). L'appel
PUT /connectors/<nom>/config est idempotent. Kafka Connect est attendu jusqu'à 120 s.
"""

import json
import sys
import time
import urllib.error
import urllib.request


def fusionner(invariant, parametres):
    redefinies = sorted(set(invariant) & set(parametres))
    if redefinies:
        raise SystemExit(f"Clés invariantes redéfinies par les paramètres : {', '.join(redefinies)}")
    return {**invariant, **parametres}


def enregistrer(url, nom, configuration):
    corps = json.dumps(configuration).encode("utf-8")
    requete = urllib.request.Request(
        f"{url.rstrip('/')}/connectors/{nom}/config",
        data=corps,
        method="PUT",
        headers={"Content-Type": "application/json", "Accept": "application/json"},
    )
    with urllib.request.urlopen(requete, timeout=30) as reponse:
        return reponse.status


def main(arguments):
    if len(arguments) != 4:
        raise SystemExit(__doc__)
    url, nom, chemin_invariant, chemin_parametres = arguments
    with open(chemin_invariant, encoding="utf-8") as fichier:
        invariant = json.load(fichier)
    with open(chemin_parametres, encoding="utf-8") as fichier:
        parametres = json.load(fichier)
    configuration = fusionner(invariant, parametres)

    echeance = time.monotonic() + 120
    while True:
        try:
            statut = enregistrer(url, nom, configuration)
            print(f"Connecteur {nom} enregistré (HTTP {statut}).")
            return
        except urllib.error.HTTPError as erreur:
            # 409 : Kafka Connect rééquilibre son groupe ; toute autre erreur HTTP est définitive.
            if erreur.code != 409 or time.monotonic() > echeance:
                raise SystemExit(f"Échec de l'enregistrement : HTTP {erreur.code} {erreur.read().decode('utf-8', 'replace')}")
        except (urllib.error.URLError, ConnectionError):
            if time.monotonic() > echeance:
                raise SystemExit(f"Kafka Connect injoignable à {url}.")
        time.sleep(2)


if __name__ == "__main__":
    main(sys.argv[1:])
