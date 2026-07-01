"""One-shot migration: sign existing model.pkl artifacts for integrity checks.

Models saved before the F-03 hardening have no ``model_sha256`` in their
metadata.json, so serving now refuses to load them (fail-closed) unless
AGRI_ALLOW_UNVERIFIED_MODEL is set. Run this ONCE to compute and store the
integrity fields for the promoted model (and every version on disk), moving
the service onto the verified path with no retrain.

Usage:
    .venv/bin/python resign_promoted.py

For the strong, tamper-resistant HMAC path, export a key first (keep it out of
the models dir and out of git):
    export AGRI_MODEL_HMAC_KEY='<random-secret>'
    .venv/bin/python resign_promoted.py

Idempotent: re-running just recomputes/overwrites the same fields.
"""
from __future__ import annotations

import json
import os

from agriforecast_ml.registry import registry


def main() -> None:
    models_dir = registry._MODELS_DIR
    keyed = bool(os.environ.get(registry._HMAC_KEY_ENV))
    print(f"Models dir: {models_dir}")
    print(f"HMAC signing: {'ON (AGRI_MODEL_HMAC_KEY set)' if keyed else 'OFF (sha256 only)'}")

    pointer = models_dir / "promoted.json"
    promoted_version = None
    if pointer.exists():
        promoted_version = json.loads(pointer.read_text()).get("version")
        print(f"Promoted version: {promoted_version}")
    else:
        print("No promoted.json found.")

    # Sign every on-disk version (promoted included). Version dirs are v<N>.
    versions = sorted(
        (p.name for p in models_dir.glob("v*") if p.name[1:].isdigit()),
        key=lambda v: int(v[1:]),
    )
    if not versions:
        print("No model versions found to sign.")
        return

    signed = 0
    for version in versions:
        try:
            fields = registry.sign_version(version)
        except FileNotFoundError as exc:
            print(f"  {version}: skipped ({exc})")
            continue
        tag = " [PROMOTED]" if version == promoted_version else ""
        hmac_note = " +hmac" if "model_hmac" in fields else ""
        print(f"  {version}: signed sha256={fields['model_sha256'][:16]}...{hmac_note}{tag}")
        signed += 1

    print(f"Done. Signed {signed} version(s).")
    if promoted_version and promoted_version not in versions:
        print(
            f"WARNING: promoted version {promoted_version!r} has no on-disk "
            f"directory — it was NOT signed."
        )


if __name__ == "__main__":
    main()
