"""Training scripts for the neural restorer.

They share the upscaler's corpus helpers, its split of the songs and its discriminators, which live in
../neural-upscaler; importing this package puts that folder on the path.
"""
import sys
from pathlib import Path

_UPSCALER = Path(__file__).resolve().parents[2] / "neural-upscaler"
if _UPSCALER.is_dir() and str(_UPSCALER) not in sys.path:
    sys.path.insert(0, str(_UPSCALER))
