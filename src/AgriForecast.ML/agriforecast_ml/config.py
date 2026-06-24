"""Database settings resolution: env vars first, then the .NET appsettings.json
connection string (single source of truth — no secrets duplicated in Python)."""
from __future__ import annotations

import os
import re
import json
from pathlib import Path


def _parse_connection_string(cs: str) -> dict:
    parts = {}
    for token in cs.split(";"):
        if "=" in token:
            k, v = token.split("=", 1)
            parts[k.strip().lower()] = v.strip()
    server = parts.get("server", "localhost,1433")
    host, _, port = server.partition(",")
    return {
        "host": host or "localhost",
        "port": int(port) if port else 1433,
        "database": parts.get("database", "AgriForecast"),
        "user": parts.get("user id", "Sa"),
        "password": parts.get("password", ""),
    }


def _from_appsettings() -> dict | None:
    # src/AgriForecast.ML/agriforecast_ml/config.py -> ../../AgriForecast.API/appsettings.json
    appsettings = Path(__file__).resolve().parents[2] / "AgriForecast.API" / "appsettings.json"
    if not appsettings.exists():
        return None
    data = json.loads(appsettings.read_text())
    cs = data.get("ConnectionStrings", {}).get("DefaultConnection")
    return _parse_connection_string(cs) if cs else None


def get_db_settings() -> dict:
    if os.getenv("AGRI_DB_HOST"):
        return {
            "host": os.getenv("AGRI_DB_HOST", "localhost"),
            "port": int(os.getenv("AGRI_DB_PORT", "1434")),
            "database": os.getenv("AGRI_DB_NAME", "AgriForecast"),
            "user": os.getenv("AGRI_DB_USER", "Sa"),
            "password": os.getenv("AGRI_DB_PASSWORD", ""),
        }
    settings = _from_appsettings()
    if settings:
        return settings
    raise RuntimeError("No DB settings: set AGRI_DB_* env vars or provide AgriForecast.API/appsettings.json")
