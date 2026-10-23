# Chargement de toutes les vérifications du répertoire (convention des vérifications externes Checkov).
from os.path import basename, dirname, isfile, join
import glob

modules = glob.glob(join(dirname(__file__), "*.py"))
__all__ = [basename(f)[:-3] for f in modules if isfile(f) and not f.endswith("__init__.py")]
