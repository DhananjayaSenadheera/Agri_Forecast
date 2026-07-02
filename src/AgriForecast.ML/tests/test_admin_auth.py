"""
AgriForecast ML -- Security regression tests for F-02: /admin/* API-key auth.

Coverage:
  TestAdminAuthRejects   -- no X-API-Key header     -> 401
                         -- wrong X-API-Key value    -> 401
  TestAdminAuthAccepts   -- correct X-API-Key        -> NOT 401/403
  TestFailClosed         -- ML_ADMIN_API_KEY unset   -> 500-class (not open)
                         -- ML_ADMIN_API_KEY empty   -> 500-class (not open)
  TestPublicUnaffected   -- /health with no header   -> 200 (public routes stay open)

Design notes
------------
- All tests use FastAPI TestClient (backed by httpx, installed as a test dep).
- The TestClient is created INSIDE each test body so that monkeypatching
  ML_ADMIN_API_KEY is already in effect before the ASGI app handles the request.
  The app module is imported once at module level; the dependency function
  (require_api_key) reads os.getenv() at call time, so per-test monkeypatching
  works without any module reimport.
- agriforecast_ml.serving.predict is stubbed at module level via sys.modules so
  that its heavyweight registry/DB call at import time never executes in this
  test file.  The stub provides just enough surface for app.py to import cleanly.
- ingest_news and score_news are stubbed inside TestAdminAuthAccepts via
  patch.dict so that the real pipeline is never invoked.
"""
from __future__ import annotations

import sys
from pathlib import Path
from types import ModuleType
from unittest.mock import MagicMock, patch

# ---------------------------------------------------------------------------
# Path setup: allow `import agriforecast_ml` from the ML project root.
# ---------------------------------------------------------------------------
ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

# ---------------------------------------------------------------------------
# Stub out agriforecast_ml.serving.predict BEFORE app.py is imported.
#
# app.py does:  from . import predict
# predict.py does at module level:  _PAYLOAD, _META = registry.load_promoted()
# which triggers DB access.  We replace the entire module with a lightweight
# stub so the serving app can be imported in a pure unit-test context.
# ---------------------------------------------------------------------------

_predict_stub = ModuleType("agriforecast_ml.serving.predict")
_predict_stub.model_info = MagicMock(return_value={"version": "v-test", "status": "stub"})
_predict_stub.predict_harvest = MagicMock(
    return_value={
        "cropId": "stub-crop",
        "cropName": "Stub",
        "plantDate": "2026-01-15",
        "harvestDate": "2026-04-15",
        "growthPeriodDays": 90,
        "predictedPrice": 100.0,
        "lowerBound": 80.0,
        "upperBound": 120.0,
        "confidence": "Medium",
        "activePredictor": "stub",
        "modelVersion": "v-test",
        "explanation": "stub",
        "topFactors": [],
    }
)
_predict_stub.timeline = MagicMock(return_value={"stub": True})

sys.modules.setdefault("agriforecast_ml.serving.predict", _predict_stub)

# Now it is safe to import the app.
from agriforecast_ml.serving.app import app  # noqa: E402
from starlette.testclient import TestClient   # noqa: E402

# ---------------------------------------------------------------------------
# Constants used across tests.
# ---------------------------------------------------------------------------
ADMIN_NEWS_PATH = "/admin/ingest-news"
CORRECT_KEY = "test-secret-key-12345"
WRONG_KEY = "not-the-right-key"
ENV_VAR = "ML_ADMIN_API_KEY"


# ===========================================================================
# 1 & 2 -- Admin route REJECTS missing / wrong key (always 401)
# ===========================================================================

class TestAdminAuthRejects:
    """Requests to /admin/* without a valid key must be rejected with 401."""

    def test_no_header_returns_401(self, monkeypatch):
        """Case 1: no X-API-Key header at all -> 401."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(ADMIN_NEWS_PATH, json={})
        assert resp.status_code == 401, (
            f"Expected 401 for missing X-API-Key, got {resp.status_code}. "
            f"Body: {resp.text}"
        )

    def test_wrong_header_returns_401(self, monkeypatch):
        """Case 2: X-API-Key header present but value is wrong -> 401."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(
            ADMIN_NEWS_PATH,
            json={},
            headers={"X-API-Key": WRONG_KEY},
        )
        assert resp.status_code == 401, (
            f"Expected 401 for wrong X-API-Key, got {resp.status_code}. "
            f"Body: {resp.text}"
        )

    def test_empty_string_header_returns_401(self, monkeypatch):
        """Edge: X-API-Key header set to empty string -> 401 (not allowed)."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(
            ADMIN_NEWS_PATH,
            json={},
            headers={"X-API-Key": ""},
        )
        assert resp.status_code == 401, (
            f"Expected 401 for empty X-API-Key string, got {resp.status_code}. "
            f"Body: {resp.text}"
        )

    def test_error_body_is_json_with_detail(self, monkeypatch):
        """401 response body must be valid JSON with a 'detail' key."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(ADMIN_NEWS_PATH, json={})
        body = resp.json()
        assert "detail" in body, (
            f"401 body must contain 'detail'. Got: {body}"
        )


# ===========================================================================
# 3 -- Admin route ACCEPTS the correct key (auth passes)
# ===========================================================================

class TestAdminAuthAccepts:
    """Correct X-API-Key must pass the auth layer (response is NOT 401 or 403)."""

    def test_correct_key_is_not_401(self, monkeypatch):
        """Case 3: correct X-API-Key -> auth passes; must NOT be 401 or 403."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)

        ingest_stub = MagicMock()
        ingest_stub.run = MagicMock(return_value={"articles": 5, "skipped": 0})
        score_stub = MagicMock()
        score_stub.run = MagicMock(return_value={"scored": 5})

        with patch.dict(sys.modules, {"ingest_news": ingest_stub, "score_news": score_stub}):
            resp = client.post(
                ADMIN_NEWS_PATH,
                json={"dryRun": True, "skipQa": True, "writebackScores": False},
                headers={"X-API-Key": CORRECT_KEY},
            )

        assert resp.status_code not in (401, 403), (
            f"Correct key must not return 401/403; got {resp.status_code}. "
            f"Body: {resp.text}"
        )

    def test_correct_key_pipeline_returns_200(self, monkeypatch):
        """With correct key and stubbed pipeline, endpoint must return 200."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)

        ingest_stub = MagicMock()
        ingest_stub.run = MagicMock(return_value={"articles": 3, "skipped": 1})
        score_stub = MagicMock()
        score_stub.run = MagicMock(return_value={"scored": 3})

        with patch.dict(sys.modules, {"ingest_news": ingest_stub, "score_news": score_stub}):
            resp = client.post(
                ADMIN_NEWS_PATH,
                json={},
                headers={"X-API-Key": CORRECT_KEY},
            )

        assert resp.status_code == 200, (
            f"Expected 200 with correct key + stubbed pipeline; "
            f"got {resp.status_code}. Body: {resp.text}"
        )
        body = resp.json()
        assert body.get("status") == "ok", f"Expected status=ok in body; got {body}"


# ===========================================================================
# 4 -- Fail-closed: ML_ADMIN_API_KEY unset or empty -> 500-class
# ===========================================================================

class TestFailClosed:
    """Server misconfiguration (env var absent/empty) must fail closed (5xx),
    never accidentally grant access."""

    def test_env_var_unset_returns_5xx(self, monkeypatch):
        """Case 4a: ML_ADMIN_API_KEY not present in environment -> 5xx."""
        monkeypatch.delenv(ENV_VAR, raising=False)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(
            ADMIN_NEWS_PATH,
            json={},
            headers={"X-API-Key": "any-key"},
        )
        assert resp.status_code >= 500, (
            f"Unset ML_ADMIN_API_KEY must return 5xx (fail-closed); "
            f"got {resp.status_code}. Body: {resp.text}"
        )

    def test_env_var_empty_returns_5xx(self, monkeypatch):
        """Case 4b: ML_ADMIN_API_KEY set to empty string -> 5xx."""
        monkeypatch.setenv(ENV_VAR, "")
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(
            ADMIN_NEWS_PATH,
            json={},
            headers={"X-API-Key": "any-key"},
        )
        assert resp.status_code >= 500, (
            f"Empty ML_ADMIN_API_KEY must return 5xx (fail-closed); "
            f"got {resp.status_code}. Body: {resp.text}"
        )

    def test_env_var_unset_does_not_return_2xx(self, monkeypatch):
        """Fail-closed: unset key must never yield a 2xx response."""
        monkeypatch.delenv(ENV_VAR, raising=False)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(
            ADMIN_NEWS_PATH,
            json={},
            headers={"X-API-Key": "anything"},
        )
        assert not (200 <= resp.status_code < 300), (
            f"Unset ML_ADMIN_API_KEY must never return 2xx; "
            f"got {resp.status_code}. Body: {resp.text}"
        )

    def test_env_var_unset_detail_describes_misconfiguration(self, monkeypatch):
        """5xx body detail must mention misconfiguration, not leak internals."""
        monkeypatch.delenv(ENV_VAR, raising=False)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(
            ADMIN_NEWS_PATH,
            json={},
            headers={"X-API-Key": "anything"},
        )
        body = resp.json()
        detail = body.get("detail", "")
        assert "misconfiguration" in detail.lower() or "not configured" in detail.lower(), (
            f"5xx detail should describe misconfiguration. Got: {detail!r}"
        )


# ===========================================================================
# 5 -- Public routes are NOT locked down by the auth change
# ===========================================================================

class TestPublicUnaffected:
    """Public routes (/health, /model-info) must work without X-API-Key
    after F-02 was applied."""

    def test_health_no_key_returns_200(self, monkeypatch):
        """Case 5: /health with no X-API-Key header -> 200."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.get("/health")
        assert resp.status_code == 200, (
            f"/health without X-API-Key must return 200; "
            f"got {resp.status_code}. Body: {resp.text}"
        )

    def test_health_with_wrong_key_still_200(self, monkeypatch):
        """Public /health must not be rejected even when X-API-Key is wrong."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.get("/health", headers={"X-API-Key": WRONG_KEY})
        assert resp.status_code == 200, (
            f"/health must be 200 regardless of X-API-Key value; "
            f"got {resp.status_code}. Body: {resp.text}"
        )

    def test_health_env_var_unset_still_200(self, monkeypatch):
        """Even with ML_ADMIN_API_KEY unset, /health must return 200.
        Confirms fail-closed logic only fires on /admin/* routes."""
        monkeypatch.delenv(ENV_VAR, raising=False)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.get("/health")
        assert resp.status_code == 200, (
            f"/health must be 200 even when ML_ADMIN_API_KEY is unset; "
            f"got {resp.status_code}. Body: {resp.text}"
        )

    def test_model_info_no_key_returns_200(self, monkeypatch):
        """/model-info is public -- no key needed."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.get("/model-info")
        assert resp.status_code == 200, (
            f"/model-info without X-API-Key must return 200; "
            f"got {resp.status_code}. Body: {resp.text}"
        )
