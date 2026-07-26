"""Minimal .env loader (stdlib only; python-dotenv is deliberately not a dependency).

Loads KEY=VALUE pairs from the gitignored src/AgriForecast.ML/.env so the service
gets its secrets however it is launched. Real environment variables always win, a
missing file is a no-op, only the fixed path next to this package is read, and
values are never logged.
"""
from __future__ import annotations

import logging
import os
from pathlib import Path

_log = logging.getLogger(__name__)

# agriforecast_ml/envfile.py -> src/AgriForecast.ML/.env
_ENV_FILE = Path(__file__).resolve().parents[1] / ".env"


def load_env_file() -> None:
    if not _ENV_FILE.exists():
        return
    loaded = 0
    for raw in _ENV_FILE.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip("'\"")
        if key and key not in os.environ:
            os.environ[key] = value
            loaded += 1
    if loaded:
        _log.info(
            "Loaded %d variable(s) from %s (values not logged).", loaded, _ENV_FILE
        )
