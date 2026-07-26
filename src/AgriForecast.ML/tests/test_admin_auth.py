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
- agriforecast_ml.serving.predict is stubbed via the module-scoped
  serving_app_with_stubbed_predict fixture (tests/conftest.py) so that its
  heavyweight registry/DB call at import time never executes in this test
  file.  The fixture restores sys.modules on teardown, so the stub cannot
  leak into other test files during a full-suite run.
- ingest_news and score_news are stubbed inside TestAdminAuthAccepts via
  patch.dict so that the real pipeline is never invoked.
"""
from __future__ import annotations

import sys
from unittest.mock import MagicMock, patch

import pytest
from starlette.testclient import TestClient


@pytest.fixture(scope="module")
def app(serving_app_with_stubbed_predict):
    """The serving app, imported with serving.predict stubbed (hermetic)."""
    return serving_app_with_stubbed_predict

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

    def test_no_header_returns_401(self, monkeypatch, app):
        """Case 1: no X-API-Key header at all -> 401."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(ADMIN_NEWS_PATH, json={})
        assert resp.status_code == 401, (
            f"Expected 401 for missing X-API-Key, got {resp.status_code}. "
            f"Body: {resp.text}"
        )

    def test_wrong_header_returns_401(self, monkeypatch, app):
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

    def test_empty_string_header_returns_401(self, monkeypatch, app):
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

    def test_error_body_is_json_with_detail(self, monkeypatch, app):
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

    def test_correct_key_is_not_401(self, monkeypatch, app):
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

    def test_correct_key_pipeline_returns_200(self, monkeypatch, app):
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

    def test_env_var_unset_returns_5xx(self, monkeypatch, app):
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

    def test_env_var_empty_returns_5xx(self, monkeypatch, app):
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

    def test_env_var_unset_does_not_return_2xx(self, monkeypatch, app):
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

    def test_env_var_unset_detail_describes_misconfiguration(self, monkeypatch, app):
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

    def test_health_no_key_returns_200(self, monkeypatch, app):
        """Case 5: /health with no X-API-Key header -> 200."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.get("/health")
        assert resp.status_code == 200, (
            f"/health without X-API-Key must return 200; "
            f"got {resp.status_code}. Body: {resp.text}"
        )

    def test_health_with_wrong_key_still_200(self, monkeypatch, app):
        """Public /health must not be rejected even when X-API-Key is wrong."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.get("/health", headers={"X-API-Key": WRONG_KEY})
        assert resp.status_code == 200, (
            f"/health must be 200 regardless of X-API-Key value; "
            f"got {resp.status_code}. Body: {resp.text}"
        )

    def test_health_env_var_unset_still_200(self, monkeypatch, app):
        """Even with ML_ADMIN_API_KEY unset, /health must return 200.
        Confirms fail-closed logic only fires on /admin/* routes."""
        monkeypatch.delenv(ENV_VAR, raising=False)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.get("/health")
        assert resp.status_code == 200, (
            f"/health must be 200 even when ML_ADMIN_API_KEY is unset; "
            f"got {resp.status_code}. Body: {resp.text}"
        )

    def test_model_info_no_key_returns_200(self, monkeypatch, app):
        """/model-info is public -- no key needed."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.get("/model-info")
        assert resp.status_code == 200, (
            f"/model-info without X-API-Key must return 200; "
            f"got {resp.status_code}. Body: {resp.text}"
        )
