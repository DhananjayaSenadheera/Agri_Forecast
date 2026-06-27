"""Lightweight file-based model registry.

models/<version>/model.pkl + metadata.json. A models/promoted.json pointer
records which version is live. No external services.
"""
from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path

import joblib

# AgriForecast.ML/models  (registry/ -> agriforecast_ml/ -> AgriForecast.ML/)
_MODELS_DIR = Path(__file__).resolve().parents[2] / "models"


def _next_version() -> str:
    _MODELS_DIR.mkdir(exist_ok=True)
    existing = [int(p.name[1:]) for p in _MODELS_DIR.glob("v*") if p.name[1:].isdigit()]
    return f"v{(max(existing) + 1) if existing else 1}"


def save_model(payload: dict, metadata: dict, promote: bool) -> str:
    version = _next_version()
    vdir = _MODELS_DIR / version
    vdir.mkdir(parents=True, exist_ok=True)
    joblib.dump(payload, vdir / "model.pkl")
    metadata = {**metadata, "version": version,
                "trained_at": datetime.now(timezone.utc).isoformat(),
                "promoted": promote}
    (vdir / "metadata.json").write_text(json.dumps(metadata, indent=2))
    if promote:
        (_MODELS_DIR / "promoted.json").write_text(json.dumps({"version": version}, indent=2))
    return version


def load_promoted():
    pointer = _MODELS_DIR / "promoted.json"
    if not pointer.exists():
        return None, None
    version = json.loads(pointer.read_text())["version"]
    vdir = _MODELS_DIR / version
    payload = joblib.load(vdir / "model.pkl")
    metadata = json.loads((vdir / "metadata.json").read_text())
    return payload, metadata


def load_promoted_metadata() -> dict | None:
    """Read the currently-promoted version's metadata WITHOUT loading the
    (heavy) model payload. Used by the retrain guardrail to compare a new
    candidate against the live predictor's recorded CV score. Returns None
    when nothing is promoted yet (first-ever training run)."""
    pointer = _MODELS_DIR / "promoted.json"
    if not pointer.exists():
        return None
    version = json.loads(pointer.read_text())["version"]
    meta_path = _MODELS_DIR / version / "metadata.json"
    if not meta_path.exists():
        return None
    return json.loads(meta_path.read_text())
