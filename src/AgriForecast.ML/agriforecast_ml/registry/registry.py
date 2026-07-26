"""Lightweight file-based model registry.

models/<version>/model.pkl plus metadata.json, with models/promoted.json pointing at the
live version. No external services.

Two security controls live here.

1. Safe deserialization. model.pkl is loaded with joblib/pickle, which executes whatever
   is embedded in the byte stream, so the artifact is verified BEFORE joblib touches it.
   save_model always records model_sha256, plus an HMAC when AGRI_MODEL_HMAC_KEY is set,
   and load_promoted recomputes over the on-disk bytes and fails CLOSED on a mismatch.
   A bare sha256 catches a stray overwrite but not an attacker who can also rewrite the
   co-located metadata.json; the keyed HMAC does, so keep that key out of the models dir.

2. Path traversal. The live version string comes from promoted.json and is joined into a
   filesystem path, so a pointer like {'version': '../../etc'} would escape models/.
   Versions are always 'v' followed by digits, and _safe_version_dir enforces that shape
   and re-checks that the resolved directory is inside models/.

Any integrity or path failure RAISES. serving/predict.py loads the promoted payload at
import, so it surfaces loudly and refuses to serve a tampered or unverifiable model.
"""
from __future__ import annotations

import hashlib
import hmac
import json
import logging
import os
import re
from datetime import datetime, timezone
from pathlib import Path

import joblib

_log = logging.getLogger(__name__)

# AgriForecast.ML/models  (registry/ -> agriforecast_ml/ -> AgriForecast.ML/)
_MODELS_DIR = Path(__file__).resolve().parents[2] / "models"

# The only shape _next_version() ever produces. Enforced on every version string that
# becomes a path, which kills '..', slashes and absolute paths.
_VERSION_RE = re.compile(r"^v\d+$")

_HMAC_KEY_ENV = "AGRI_MODEL_HMAC_KEY"
_ALLOW_UNVERIFIED_ENV = "AGRI_ALLOW_UNVERIFIED_MODEL"


def _next_version() -> str:
    _MODELS_DIR.mkdir(exist_ok=True)
    existing = [int(p.name[1:]) for p in _MODELS_DIR.glob("v*") if p.name[1:].isdigit()]
    return f"v{(max(existing) + 1) if existing else 1}"


def _safe_version_dir(version: str) -> Path:
    """Resolve models/<version> safely, or raise ValueError.

    Two independent gates so neither is load-bearing alone: the version must be 'v' followed
    by digits (rejecting traversal, slashes and absolute paths), and the resolved directory
    must be contained within _MODELS_DIR.
    """
    if not isinstance(version, str) or not _VERSION_RE.match(version):
        raise ValueError(
            f"Refusing unsafe model version {version!r}: expected 'v<N>' "
            f"(e.g. 'v9'). This blocks path traversal via promoted.json."
        )
    base = _MODELS_DIR.resolve()
    vdir = (base / version).resolve()
    if not vdir.is_relative_to(base):
        raise ValueError(
            f"Model version {version!r} resolves outside the models directory "
            f"({vdir} not under {base}) — refusing."
        )
    return vdir


def _pkl_digest(pkl_path: Path, hmac_key: bytes | None = None) -> tuple[str, str | None]:
    """Return (sha256_hex, hmac_sha256_hex_or_None) over the file's bytes.

    One implementation shared by save_model, sign_version and load_promoted, so signing and
    verification can never drift.
    """
    data = pkl_path.read_bytes()
    sha = hashlib.sha256(data).hexdigest()
    mac = hmac.new(hmac_key, data, hashlib.sha256).hexdigest() if hmac_key else None
    return sha, mac


def _hmac_key() -> bytes | None:
    val = os.environ.get(_HMAC_KEY_ENV)
    return val.encode("utf-8") if val else None


def _env_truthy(val: str | None) -> bool:
    return str(val).strip().lower() in {"1", "true", "yes"} if val is not None else False


def _integrity_fields(pkl_path: Path) -> dict:
    """Compute the metadata integrity fields for a written model.pkl."""
    sha, mac = _pkl_digest(pkl_path, _hmac_key())
    fields = {"model_sha256": sha}
    if mac is not None:
        fields["model_hmac"] = mac
    return fields


def sign_version(version: str) -> dict:
    """Recompute the integrity fields for a version and write them into its metadata.json.

    Existing keys are preserved. Returns the fields that were written.
    """
    vdir = _safe_version_dir(version)
    pkl_path = vdir / "model.pkl"
    meta_path = vdir / "metadata.json"
    if not pkl_path.exists():
        raise FileNotFoundError(f"No model.pkl for version {version!r} at {pkl_path}")
    if not meta_path.exists():
        raise FileNotFoundError(f"No metadata.json for version {version!r} at {meta_path}")
    fields = _integrity_fields(pkl_path)
    meta = json.loads(meta_path.read_text())
    meta.update(fields)
    meta_path.write_text(json.dumps(meta, indent=2))
    return fields


def save_model(payload: dict, metadata: dict, promote: bool) -> str:
    version = _next_version()
    vdir = _MODELS_DIR / version
    vdir.mkdir(parents=True, exist_ok=True)
    pkl_path = vdir / "model.pkl"
    joblib.dump(payload, pkl_path)
    # Integrity fields computed over the freshly-written pkl bytes and baked
    # into metadata.json (written just below).
    integrity = _integrity_fields(pkl_path)
    metadata = {**metadata, "version": version,
                "trained_at": datetime.now(timezone.utc).isoformat(),
                "promoted": promote,
                **integrity}
    (vdir / "metadata.json").write_text(json.dumps(metadata, indent=2))
    if promote:
        (_MODELS_DIR / "promoted.json").write_text(json.dumps({"version": version}, indent=2))
    # Write this version's ModelTrainingRuns row and re-sync the Promoted flags, AFTER the
    # version dir, metadata and promoted.json pointer are final, so Promoted is derived from
    # the live pointer and never echoed from metadata. FAIL-OPEN: a DB hiccup must never cost
    # a finished model, and this outer guard is the last resort in case the hook's own error
    # handling has a bug.
    try:
        _record_training_run(metadata)
    except Exception:
        _log.warning("training_log hook failed unexpectedly; model save unaffected")
    return version


def _current_promoted_version() -> "str | None":
    """The live version from promoted.json, or None if nothing is promoted.
    Read AFTER save_model has (possibly) rewritten the pointer, so it reflects
    the final promotion decision -- the single source of truth for Promoted."""
    pointer = _MODELS_DIR / "promoted.json"
    if not pointer.exists():
        return None
    try:
        return json.loads(pointer.read_text()).get("version")
    except (ValueError, OSError):
        return None


def _record_training_run(metadata: dict) -> None:
    """Fail-open hook: upsert this version's ModelTrainingRuns row and re-sync Promoted flags.

    Any failure (no DB configured, DB down, table absent) is swallowed with a redacted
    warning - the training log is best-effort and must never break saving or promotion.
    Kept as a module-level function so tests can neutralise it and stay hermetic.
    """
    try:
        from ..db import get_engine
        from .. import training_log

        engine = get_engine()
        training_log.upsert_training_run(
            engine, **training_log.values_from_metadata(metadata)
        )
        live = _current_promoted_version()
        if live is not None:
            training_log.sync_promoted_flags(engine, live)
    except Exception as exc:  # noqa: BLE001 -- fail-open; training must not break
        try:
            from .. import training_log
            detail = training_log.redact_sensitive(str(exc))
        except Exception:  # noqa: BLE001 -- redaction itself must never raise here
            detail = "<unavailable>"
        _log.warning(
            "training_log write skipped (%s): %s -- training/save unaffected.",
            type(exc).__name__, detail,
        )


def _verify_integrity(pkl_path: Path, metadata: dict, version: str) -> None:
    """Fail CLOSED unless pkl_path matches the integrity fields in metadata.

    Raises RuntimeError on any mismatch, or on an unverifiable legacy model unless the
    operator opts in with AGRI_ALLOW_UNVERIFIED_MODEL. Precedence: HMAC, then sha256, then
    the legacy escape hatch.
    """
    stored_hmac = metadata.get("model_hmac")
    stored_sha = metadata.get("model_sha256")
    key = _hmac_key()

    # 1. Strong path: keyed HMAC present and we hold the key.
    if stored_hmac and key is not None:
        _, actual_hmac = _pkl_digest(pkl_path, key)
        if not hmac.compare_digest(str(stored_hmac), str(actual_hmac)):
            raise RuntimeError(
                f"Model integrity check FAILED for version {version!r}: "
                f"HMAC mismatch — {pkl_path} does not match the signed value in "
                f"metadata.json. Refusing to load (possible tampering)."
            )
        return

    # 2. sha256 path: detects tampering, but NOT an attacker who also rewrote
    #    the co-located metadata. Recommend HMAC.
    if stored_sha:
        actual_sha, _ = _pkl_digest(pkl_path)
        if not hmac.compare_digest(str(stored_sha), str(actual_sha)):
            raise RuntimeError(
                f"Model integrity check FAILED for version {version!r}: "
                f"sha256 mismatch — {pkl_path} does not match metadata.json. "
                f"Refusing to load (possible tampering)."
            )
        if key is None:
            _log.warning(
                "Model %s verified by sha256 only. metadata.json is co-located "
                "with model.pkl, so an attacker able to rewrite the pkl could "
                "also rewrite its hash. Set %s and run resign_promoted.py to "
                "enable tamper-resistant HMAC signing.", version, _HMAC_KEY_ENV)
        return

    # 3. Legacy model with no stored hash: refuse by default; explicit opt-in
    #    lets the operator load a pre-hash model without hard-breaking serving.
    if _env_truthy(os.environ.get(_ALLOW_UNVERIFIED_ENV)):
        _log.warning(
            "LOADING UNVERIFIED MODEL %s: metadata.json has no model_sha256/"
            "model_hmac and %s is set. This model's bytes are NOT integrity-"
            "checked — arbitrary code in a tampered model.pkl would execute. "
            "Run resign_promoted.py to sign it and remove this override.",
            version, _ALLOW_UNVERIFIED_ENV)
        return

    raise RuntimeError(
        f"Model version {version!r} has no integrity fields (model_sha256/"
        f"model_hmac) in metadata.json — refusing to unpickle an unverifiable "
        f"artifact. Run resign_promoted.py to sign the promoted model (optionally "
        f"set {_HMAC_KEY_ENV} first for the strong HMAC path), or set "
        f"{_ALLOW_UNVERIFIED_ENV}=1 to load it unverified (NOT recommended)."
    )


def load_promoted():
    pointer = _MODELS_DIR / "promoted.json"
    if not pointer.exists():
        return None, None
    version = json.loads(pointer.read_text())["version"]
    vdir = _safe_version_dir(version)  # rejects traversal before any path use
    pkl_path = vdir / "model.pkl"
    metadata = json.loads((vdir / "metadata.json").read_text())
    # Verify bytes on disk BEFORE joblib (== pickle) executes anything.
    _verify_integrity(pkl_path, metadata, version)
    payload = joblib.load(pkl_path)
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
    vdir = _safe_version_dir(version)  # rejects traversal before any path use
    meta_path = vdir / "metadata.json"
    if not meta_path.exists():
        return None
    return json.loads(meta_path.read_text())
