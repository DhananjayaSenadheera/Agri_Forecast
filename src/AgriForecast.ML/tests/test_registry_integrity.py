"""Security regression tests for the model registry's integrity and path guards.

save_model always writes model_sha256, plus model_hmac when AGRI_MODEL_HMAC_KEY is set.
load_promoted re-derives both over the on-disk bytes and fails CLOSED before joblib.load
runs, so a tampered pkl is never unpickled. _safe_version_dir rejects any version string
that is not 'v' followed by digits and checks the resolved directory stays inside the
models dir, so a crafted promoted.json cannot traverse out.

Covers the happy path, a tampered pkl, a legacy model with no hash (refused by default,
allowed with the explicit env opt-in), the HMAC path including a wrong key, path traversal,
and re-signing a legacy model.

Isolation: fixtures point _MODELS_DIR at tmp_path and clear both env vars, so the real
models tree is never touched and env state cannot bleed between tests.
"""
from __future__ import annotations

import json
import logging
import sys
from pathlib import Path

import pytest

# Path setup: allow `import agriforecast_ml` from the ML project root.
ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml.registry import registry  # noqa: E402

# Constants
HMAC_ENV = "AGRI_MODEL_HMAC_KEY"
ALLOW_ENV = "AGRI_ALLOW_UNVERIFIED_MODEL"
DUMMY_PAYLOAD = {"crop": "tomato", "weights": [1.0, 2.5, 0.7]}
DUMMY_META = {"algo": "xgboost", "cv_mae": 12.3}


# Shared fixtures


@pytest.fixture(autouse=True)
def clean_env(monkeypatch):
    """Remove both security env vars before every test so tests are isolated."""
    monkeypatch.delenv(HMAC_ENV, raising=False)
    monkeypatch.delenv(ALLOW_ENV, raising=False)


@pytest.fixture()
def models_dir(tmp_path, monkeypatch):
    """Redirect registry._MODELS_DIR to a fresh tmp_path sub-directory.

    CRITICAL: this fixture must be used in EVERY test so the real models/
    tree is never read or written.
    """
    fake = tmp_path / "models"
    fake.mkdir()
    monkeypatch.setattr(registry, "_MODELS_DIR", fake)
    # Neutralise the admin Logs-hub training-log hook (PR B): save_model now
    # writes a ModelTrainingRuns row via _record_training_run. It is fail-open,
    # but we must never let a save_model test attempt a real DB connection, so
    # stub it out here in the same sandbox fixture. (training_log's own SQL is
    # covered hermetically in test_training_log.py.)
    monkeypatch.setattr(registry, "_record_training_run", lambda *a, **k: None)
    return fake


# TestF03RegistryIntegrity


class TestF03RegistryIntegrity:
    """Standing security regression suite for F-03."""

    # 1. Happy path: save -> load returns the payload; sha256 written

    def test_happy_path_load_returns_payload(self, models_dir):
        """save_model + load_promoted round-trip returns the original payload."""
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        payload, meta = registry.load_promoted()
        assert payload == DUMMY_PAYLOAD, (
            f"load_promoted returned {payload!r}, expected {DUMMY_PAYLOAD!r}"
        )

    def test_happy_path_metadata_has_sha256(self, models_dir):
        """After save_model the version's metadata.json must contain model_sha256."""
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        meta_path = models_dir / version / "metadata.json"
        meta = json.loads(meta_path.read_text())
        assert "model_sha256" in meta, (
            f"metadata.json must contain model_sha256 after save_model. "
            f"Got keys: {list(meta.keys())}"
        )
        # The hash must be a 64-char hex string (sha256).
        sha = meta["model_sha256"]
        assert isinstance(sha, str) and len(sha) == 64 and all(c in "0123456789abcdef" for c in sha), (
            f"model_sha256 must be a 64-char lowercase hex string; got {sha!r}"
        )

    def test_happy_path_promoted_json_written(self, models_dir):
        """save_model(promote=True) must write promoted.json pointing to the version."""
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        pointer = models_dir / "promoted.json"
        assert pointer.exists(), "promoted.json must be created when promote=True"
        data = json.loads(pointer.read_text())
        assert data.get("version") == version, (
            f"promoted.json must point to {version!r}, got {data!r}"
        )

    # 2. Tampered pkl -> refused

    def test_tampered_pkl_raises(self, models_dir):
        """Flipping a byte in model.pkl after save must cause load_promoted to raise."""
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        pkl_path = models_dir / version / "model.pkl"

        # Corrupt a byte near the middle of the file.
        raw = bytearray(pkl_path.read_bytes())
        mid = len(raw) // 2
        raw[mid] ^= 0xFF
        pkl_path.write_bytes(bytes(raw))

        with pytest.raises((RuntimeError, ValueError)):
            registry.load_promoted()

    def test_tampered_pkl_error_does_not_leak_hash_bytes(self, models_dir):
        """The integrity error message must be a plain refusal, not raw hash values
        or internal byte representations (i.e. no b'...' repr in the message)."""
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        pkl_path = models_dir / version / "model.pkl"
        raw = bytearray(pkl_path.read_bytes())
        raw[len(raw) // 2] ^= 0xFF
        pkl_path.write_bytes(bytes(raw))

        with pytest.raises((RuntimeError, ValueError)) as exc_info:
            registry.load_promoted()

        msg = str(exc_info.value)
        # Must not contain raw bytes repr like b'\x...' or b"..."
        assert "b'" not in msg and 'b"' not in msg, (
            f"Error message leaks raw byte repr: {msg!r}"
        )
        # Must contain a human-readable refusal keyword.
        assert any(kw in msg.lower() for kw in ("mismatch", "tamper", "fail", "refus", "integrity")), (
            f"Error message should describe the integrity failure clearly: {msg!r}"
        )

    # 3. Missing hash (legacy) -> refused by default

    def test_missing_hash_refused_by_default(self, models_dir):
        """A model without model_sha256 in metadata must be refused (fail-closed)."""
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        meta_path = models_dir / version / "metadata.json"
        meta = json.loads(meta_path.read_text())
        # Simulate a legacy (pre-F03) model by removing the integrity fields.
        meta.pop("model_sha256", None)
        meta.pop("model_hmac", None)
        meta_path.write_text(json.dumps(meta))

        with pytest.raises((RuntimeError, ValueError)):
            registry.load_promoted()

    def test_missing_hash_error_mentions_integrity(self, models_dir):
        """The 'no hash' refusal message must guide the operator to resign_promoted.py."""
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        meta_path = models_dir / version / "metadata.json"
        meta = json.loads(meta_path.read_text())
        meta.pop("model_sha256", None)
        meta.pop("model_hmac", None)
        meta_path.write_text(json.dumps(meta))

        with pytest.raises((RuntimeError, ValueError)) as exc_info:
            registry.load_promoted()

        msg = str(exc_info.value)
        # Must not silently pass through – message must describe the situation.
        assert any(kw in msg.lower() for kw in ("no integrity", "unverifi", "sha256", "sign", "resign", "refus")), (
            f"Error should direct operator to sign the model. Got: {msg!r}"
        )

    # 4. Legacy + escape hatch (AGRI_ALLOW_UNVERIFIED_MODEL=1)

    def test_legacy_escape_hatch_allows_load(self, models_dir, monkeypatch):
        """With AGRI_ALLOW_UNVERIFIED_MODEL=1 and no hash, load_promoted must succeed."""
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        meta_path = models_dir / version / "metadata.json"
        meta = json.loads(meta_path.read_text())
        meta.pop("model_sha256", None)
        meta.pop("model_hmac", None)
        meta_path.write_text(json.dumps(meta))

        monkeypatch.setenv(ALLOW_ENV, "1")
        payload, _ = registry.load_promoted()
        assert payload == DUMMY_PAYLOAD, (
            f"Escape hatch should return the payload; got {payload!r}"
        )

    def test_legacy_escape_hatch_emits_warning(self, models_dir, monkeypatch, caplog):
        """With AGRI_ALLOW_UNVERIFIED_MODEL=1 the loader must emit a WARNING."""
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        meta_path = models_dir / version / "metadata.json"
        meta = json.loads(meta_path.read_text())
        meta.pop("model_sha256", None)
        meta.pop("model_hmac", None)
        meta_path.write_text(json.dumps(meta))

        monkeypatch.setenv(ALLOW_ENV, "1")
        with caplog.at_level(logging.WARNING, logger="agriforecast_ml.registry.registry"):
            registry.load_promoted()

        warning_msgs = [r.message for r in caplog.records if r.levelno >= logging.WARNING]
        assert warning_msgs, (
            "load_promoted with AGRI_ALLOW_UNVERIFIED_MODEL=1 must emit at least one WARNING"
        )
        combined = " ".join(warning_msgs).lower()
        assert any(kw in combined for kw in ("unverified", "not integrity", "tamper", "resign")), (
            f"Warning must mention the risk. Got: {warning_msgs!r}"
        )

    def test_escape_hatch_values(self, models_dir, monkeypatch):
        """AGRI_ALLOW_UNVERIFIED_MODEL must accept '1', 'true', 'yes'."""
        for truthy_val in ("1", "true", "True", "yes", "YES"):
            # Fresh tmp dir each iteration would be ideal but monkeypatch doesn't
            # support that inline; instead recreate the version dir manually.
            version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
            meta_path = models_dir / version / "metadata.json"
            meta = json.loads(meta_path.read_text())
            meta.pop("model_sha256", None)
            meta.pop("model_hmac", None)
            meta_path.write_text(json.dumps(meta))

            monkeypatch.setenv(ALLOW_ENV, truthy_val)
            payload, _ = registry.load_promoted()
            assert payload == DUMMY_PAYLOAD, f"Escape hatch failed for value {truthy_val!r}"

    # 5. HMAC strong path

    def test_hmac_save_writes_model_hmac(self, models_dir, monkeypatch):
        """save_model with AGRI_MODEL_HMAC_KEY set must write model_hmac into metadata."""
        monkeypatch.setenv(HMAC_ENV, "test-key-123")
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        meta = json.loads((models_dir / version / "metadata.json").read_text())
        assert "model_hmac" in meta, (
            f"model_hmac missing from metadata when HMAC key is set. Keys: {list(meta.keys())}"
        )
        assert "model_sha256" in meta, (
            "model_sha256 must also be present alongside model_hmac"
        )

    def test_hmac_load_succeeds_with_correct_key(self, models_dir, monkeypatch):
        """load_promoted with the same key used at save time must succeed."""
        monkeypatch.setenv(HMAC_ENV, "test-key-123")
        registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        payload, _ = registry.load_promoted()
        assert payload == DUMMY_PAYLOAD, (
            f"HMAC-verified load should return the original payload; got {payload!r}"
        )

    def test_hmac_tampered_pkl_raises(self, models_dir, monkeypatch):
        """After HMAC-signed save, tampering model.pkl must make load_promoted raise."""
        monkeypatch.setenv(HMAC_ENV, "test-key-123")
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        pkl_path = models_dir / version / "model.pkl"
        raw = bytearray(pkl_path.read_bytes())
        raw[len(raw) // 2] ^= 0xFF
        pkl_path.write_bytes(bytes(raw))

        with pytest.raises((RuntimeError, ValueError)) as exc_info:
            registry.load_promoted()
        msg = str(exc_info.value).lower()
        assert any(kw in msg for kw in ("hmac", "mismatch", "tamper", "integrity", "fail", "refus")), (
            f"HMAC mismatch error must be explicit; got: {str(exc_info.value)!r}"
        )

    def test_hmac_wrong_key_at_load_raises(self, models_dir, monkeypatch):
        """Loading with a DIFFERENT HMAC key than was used at save must raise,
        proving the stored HMAC is key-specific."""
        monkeypatch.setenv(HMAC_ENV, "save-key")
        registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)

        # Now swap to a different key and attempt to load.
        monkeypatch.setenv(HMAC_ENV, "different-key-999")
        with pytest.raises((RuntimeError, ValueError)):
            registry.load_promoted()

    # 6. Path traversal: bad versions in promoted.json -> ValueError

    def _poison_promoted(self, models_dir: Path, bad_version: str) -> None:
        """Write a promoted.json pointing at an attacker-controlled version string."""
        (models_dir / "promoted.json").write_text(
            json.dumps({"version": bad_version})
        )

    def test_traversal_dotdot_evil(self, models_dir):
        """../evil in promoted.json must raise ValueError before any disk access."""
        self._poison_promoted(models_dir, "../evil")
        with pytest.raises(ValueError):
            registry.load_promoted()

    def test_traversal_nested_dotdot(self, models_dir):
        """v1/../../x must be rejected."""
        self._poison_promoted(models_dir, "v1/../../x")
        with pytest.raises(ValueError):
            registry.load_promoted()

    def test_traversal_absolute_path(self, models_dir):
        """/abs must be rejected."""
        self._poison_promoted(models_dir, "/abs")
        with pytest.raises(ValueError):
            registry.load_promoted()

    def test_traversal_shell_injection(self, models_dir):
        """'v1; rm' must be rejected (not a valid version)."""
        self._poison_promoted(models_dir, "v1; rm")
        with pytest.raises(ValueError):
            registry.load_promoted()

    def test_traversal_url_encoded_slash(self, models_dir):
        """'..%2f' must be rejected."""
        self._poison_promoted(models_dir, "..%2f")
        with pytest.raises(ValueError):
            registry.load_promoted()

    def test_traversal_load_metadata_also_blocked(self, models_dir):
        """load_promoted_metadata must also reject a traversal version string."""
        self._poison_promoted(models_dir, "../evil")
        with pytest.raises(ValueError):
            registry.load_promoted_metadata()

    def test_valid_version_accepted(self, models_dir):
        """_safe_version_dir must accept a legitimate 'v<N>' without raising."""
        # Just assert the call doesn't raise; no model files need to exist.
        vdir = registry._safe_version_dir("v1")
        assert vdir.name == "v1", f"Expected dir named 'v1', got {vdir.name!r}"
        assert str(models_dir) in str(vdir), (
            f"Resolved dir {vdir} should be inside models_dir {models_dir}"
        )

    def test_valid_version_larger_number(self, models_dir):
        """'v99' and 'v100' must both be accepted by the sanitiser."""
        for v in ("v99", "v100"):
            vdir = registry._safe_version_dir(v)
            assert vdir.name == v

    # 7. sign_version round-trip (resign migration path)

    def test_sign_version_adds_sha256_to_legacy_model(self, models_dir):
        """sign_version must add model_sha256 to a pre-F03 model that lacks it."""
        # Create a "legacy" model: saved without integrity fields.
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        meta_path = models_dir / version / "metadata.json"
        meta = json.loads(meta_path.read_text())
        meta.pop("model_sha256", None)
        meta.pop("model_hmac", None)
        meta_path.write_text(json.dumps(meta))

        # Confirm it's unsigned (would be refused without ALLOW_UNVERIFIED).
        with pytest.raises((RuntimeError, ValueError)):
            registry.load_promoted()

        # Run the resign migration.
        fields = registry.sign_version(version)
        assert "model_sha256" in fields, f"sign_version must return model_sha256; got {fields}"

        # Now load must succeed without any escape-hatch env var.
        payload, _ = registry.load_promoted()
        assert payload == DUMMY_PAYLOAD, (
            f"After sign_version, load_promoted must return the original payload; "
            f"got {payload!r}"
        )

    def test_sign_version_preserves_existing_metadata_keys(self, models_dir):
        """sign_version must NOT remove existing metadata keys (e.g. cv_mae)."""
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        meta_path = models_dir / version / "metadata.json"
        # Manually remove hashes to simulate legacy, then re-sign.
        meta = json.loads(meta_path.read_text())
        meta.pop("model_sha256", None)
        meta.pop("model_hmac", None)
        meta_path.write_text(json.dumps(meta))

        registry.sign_version(version)

        # All original DUMMY_META keys must still be in the file.
        meta_after = json.loads(meta_path.read_text())
        for key, val in DUMMY_META.items():
            assert key in meta_after, (
                f"sign_version dropped existing key {key!r} from metadata.json"
            )
            assert meta_after[key] == val, (
                f"sign_version changed value of {key!r}: expected {val!r}, "
                f"got {meta_after[key]!r}"
            )

    def test_sign_version_with_hmac_key_enables_strong_path(self, models_dir, monkeypatch):
        """sign_version run with AGRI_MODEL_HMAC_KEY writes model_hmac so the strong
        HMAC path is used on the next load."""
        version = registry.save_model(DUMMY_PAYLOAD, DUMMY_META.copy(), promote=True)
        meta_path = models_dir / version / "metadata.json"
        # Strip hashes.
        meta = json.loads(meta_path.read_text())
        meta.pop("model_sha256", None)
        meta.pop("model_hmac", None)
        meta_path.write_text(json.dumps(meta))

        monkeypatch.setenv(HMAC_ENV, "resign-key-abc")
        fields = registry.sign_version(version)
        assert "model_hmac" in fields, (
            "sign_version with HMAC key must include model_hmac in returned fields"
        )

        # Load with the same key -> success.
        payload, _ = registry.load_promoted()
        assert payload == DUMMY_PAYLOAD
