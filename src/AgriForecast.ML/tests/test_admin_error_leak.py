"""
AgriForecast ML -- Security regression tests for F-04: no exception leak in
/admin/ingest-news error responses.

What F-04 fixed
---------------
The handler used to format raw exception text into the HTTPException detail::

    raise HTTPException(status_code=502, detail=f"News ingestion failed: {exc}")

It now returns only fixed generic strings::

    raise HTTPException(status_code=502, detail="News ingestion failed.")

so that stack traces, file paths, connection strings, or any other internal
detail carried in an exception message can never reach an HTTP client.

Coverage
--------
1. ingest_news.run raises -> 502, generic message, NO sentinel text in body.
2. score_news.run raises  -> 502, generic message, NO sentinel text in body.
3. Module import fails    -> 503, generic message, NO sentinel text in body.
   Simulated by injecting a broken stub into sys.modules whose attribute
   access raises ImportError carrying the sentinel.

Design notes
------------
- Same TestClient / monkeypatch pattern as test_admin_auth.py.
- The predict stub is registered in sys.modules BEFORE importing the app so
  that the DB-hitting import inside predict.py never runs in this test file.
- A valid X-API-Key is sent on every request so auth passes and the handler
  body is actually reached.  Without it we\'d get 401 and never exercise the
  error path.
- raise_server_exceptions=False is set so FastAPI\'s HTTPException handler
  returns the JSON response rather than the TestClient re-raising in-process.
"""
from __future__ import annotations

import sys
from pathlib import Path
from types import ModuleType
from unittest.mock import MagicMock

import pytest

# ---------------------------------------------------------------------------
# Path setup -- allow `import agriforecast_ml` from the ML project root.
# ---------------------------------------------------------------------------
ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

# ---------------------------------------------------------------------------
# Stub out agriforecast_ml.serving.predict BEFORE app.py is imported so the
# registry / DB call at module level in predict.py never executes here.
# This is the same pattern used in test_admin_auth.py.
# ---------------------------------------------------------------------------
_predict_stub = ModuleType("agriforecast_ml.serving.predict")
_predict_stub.model_info = MagicMock(return_value={"version": "v-test", "status": "stub"})
_predict_stub.predict_harvest = MagicMock(return_value={
    "cropId": "stub-crop", "cropName": "Stub",
    "plantDate": "2026-01-15", "harvestDate": "2026-04-15",
    "growthPeriodDays": 90, "predictedPrice": 100.0,
    "lowerBound": 80.0, "upperBound": 120.0,
    "confidence": "Medium", "activePredictor": "stub",
    "modelVersion": "v-test", "explanation": "stub", "topFactors": [],
})
_predict_stub.timeline = MagicMock(return_value={"stub": True})
sys.modules.setdefault("agriforecast_ml.serving.predict", _predict_stub)

from agriforecast_ml.serving.app import app  # noqa: E402
from starlette.testclient import TestClient    # noqa: E402

# ---------------------------------------------------------------------------
# Constants shared across all tests.
# ---------------------------------------------------------------------------
ADMIN_NEWS_PATH = "/admin/ingest-news"
ENV_VAR         = "ML_ADMIN_API_KEY"
CORRECT_KEY     = "test-secret-key-f04"

# A sentinel that must NEVER appear in the HTTP response body.  It is embedded
# in every exception raised by the mocked pipeline functions to represent the
# kind of internal detail (paths, credentials, stack context) that the old
# code would have leaked.
SENTINEL = "SECRET_LEAK_/etc/passwd:connstr=pw123"


# ===========================================================================
# Helper
# ===========================================================================

def _make_client(monkeypatch) -> TestClient:
    """Return a TestClient with ML_ADMIN_API_KEY set to CORRECT_KEY."""
    monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
    return TestClient(app, raise_server_exceptions=False)


def _auth_headers() -> dict:
    return {"X-API-Key": CORRECT_KEY}


def _assert_no_sentinel(body_text: str) -> None:
    """Assert the sentinel string is absent from the raw response body."""
    assert SENTINEL not in body_text, (
        f"F-04 regression: exception detail leaked into HTTP response.\n"
        f"Sentinel {SENTINEL!r} found in body:\n{body_text}"
    )
    # Also make sure the individual recognisable fragments are absent.
    for fragment in ("/etc/passwd", "connstr=pw123", "SECRET_LEAK"):
        assert fragment not in body_text, (
            f"F-04 regression: fragment {fragment!r} leaked into HTTP response.\n"
            f"Body: {body_text}"
        )


# ===========================================================================
# Test class
# ===========================================================================

class TestF04NoExceptionLeak:
    """F-04 regression: /admin/ingest-news must never leak exception detail."""

    # -----------------------------------------------------------------------
    # Case 1 -- ingest_news.run raises -> 502, no sentinel
    # -----------------------------------------------------------------------

    def test_ingest_run_raises_returns_502(self, monkeypatch):
        """When ingest_news.run raises, the response status must be 502."""
        client = _make_client(monkeypatch)

        ingest_stub = MagicMock()
        ingest_stub.run = MagicMock(side_effect=RuntimeError(SENTINEL))
        score_stub = MagicMock()
        score_stub.run = MagicMock(return_value={"scored": 0})

        with pytest.MonkeyPatch.context() as mp:
            mp.setitem(sys.modules, "ingest_news", ingest_stub)
            mp.setitem(sys.modules, "score_news",  score_stub)
            resp = client.post(ADMIN_NEWS_PATH, json={}, headers=_auth_headers())

        assert resp.status_code == 502, (
            f"Expected 502 when ingest_news.run raises; got {resp.status_code}.\n"
            f"Body: {resp.text}"
        )

    def test_ingest_run_raises_no_sentinel_in_body(self, monkeypatch):
        """When ingest_news.run raises, the sentinel must NOT appear in the body."""
        client = _make_client(monkeypatch)

        ingest_stub = MagicMock()
        ingest_stub.run = MagicMock(side_effect=RuntimeError(SENTINEL))
        score_stub = MagicMock()
        score_stub.run = MagicMock(return_value={"scored": 0})

        with pytest.MonkeyPatch.context() as mp:
            mp.setitem(sys.modules, "ingest_news", ingest_stub)
            mp.setitem(sys.modules, "score_news",  score_stub)
            resp = client.post(ADMIN_NEWS_PATH, json={}, headers=_auth_headers())

        _assert_no_sentinel(resp.text)

    def test_ingest_run_raises_detail_is_generic(self, monkeypatch):
        """When ingest_news.run raises, detail must equal the fixed generic string."""
        client = _make_client(monkeypatch)

        ingest_stub = MagicMock()
        ingest_stub.run = MagicMock(side_effect=RuntimeError(SENTINEL))
        score_stub = MagicMock()
        score_stub.run = MagicMock(return_value={"scored": 0})

        with pytest.MonkeyPatch.context() as mp:
            mp.setitem(sys.modules, "ingest_news", ingest_stub)
            mp.setitem(sys.modules, "score_news",  score_stub)
            resp = client.post(ADMIN_NEWS_PATH, json={}, headers=_auth_headers())

        body = resp.json()
        assert body.get("detail") == "News ingestion failed.", (
            f"Expected detail='News ingestion failed.' but got {body.get('detail')!r}"
        )

    # -----------------------------------------------------------------------
    # Case 2 -- score_news.run raises -> 502, no sentinel
    # -----------------------------------------------------------------------

    def test_score_run_raises_returns_502(self, monkeypatch):
        """When score_news.run raises, the response status must be 502."""
        client = _make_client(monkeypatch)

        ingest_stub = MagicMock()
        ingest_stub.run = MagicMock(return_value={"articles": 1, "skipped": 0})
        score_stub = MagicMock()
        score_stub.run = MagicMock(side_effect=RuntimeError(SENTINEL))

        with pytest.MonkeyPatch.context() as mp:
            mp.setitem(sys.modules, "ingest_news", ingest_stub)
            mp.setitem(sys.modules, "score_news",  score_stub)
            resp = client.post(ADMIN_NEWS_PATH, json={}, headers=_auth_headers())

        assert resp.status_code == 502, (
            f"Expected 502 when score_news.run raises; got {resp.status_code}.\n"
            f"Body: {resp.text}"
        )

    def test_score_run_raises_no_sentinel_in_body(self, monkeypatch):
        """When score_news.run raises, the sentinel must NOT appear in the body."""
        client = _make_client(monkeypatch)

        ingest_stub = MagicMock()
        ingest_stub.run = MagicMock(return_value={"articles": 1, "skipped": 0})
        score_stub = MagicMock()
        score_stub.run = MagicMock(side_effect=RuntimeError(SENTINEL))

        with pytest.MonkeyPatch.context() as mp:
            mp.setitem(sys.modules, "ingest_news", ingest_stub)
            mp.setitem(sys.modules, "score_news",  score_stub)
            resp = client.post(ADMIN_NEWS_PATH, json={}, headers=_auth_headers())

        _assert_no_sentinel(resp.text)

    def test_score_run_raises_detail_is_generic(self, monkeypatch):
        """When score_news.run raises, detail must equal the fixed generic string."""
        client = _make_client(monkeypatch)

        ingest_stub = MagicMock()
        ingest_stub.run = MagicMock(return_value={"articles": 1, "skipped": 0})
        score_stub = MagicMock()
        score_stub.run = MagicMock(side_effect=RuntimeError(SENTINEL))

        with pytest.MonkeyPatch.context() as mp:
            mp.setitem(sys.modules, "ingest_news", ingest_stub)
            mp.setitem(sys.modules, "score_news",  score_stub)
            resp = client.post(ADMIN_NEWS_PATH, json={}, headers=_auth_headers())

        body = resp.json()
        assert body.get("detail") == "News scoring failed.", (
            f"Expected detail='News scoring failed.' but got {body.get('detail')!r}"
        )

    # -----------------------------------------------------------------------
    # Case 3 -- module import fails -> 503, no sentinel
    #
    # The handler does:
    #   try:
    #       import ingest_news
    #       import score_news
    #   except Exception as exc:
    #       raise HTTPException(status_code=503, detail="News pipeline module unavailable.")
    #
    # We simulate this by injecting a sentinel-carrying ImportError into
    # sys.modules for "ingest_news" (a value of None causes Python to raise
    # ImportError when the module is imported).  However, the lazy `import`
    # inside the handler hits the cache: if a stub was already injected from a
    # previous test, Python won\'t re-execute the import.  Instead we inject a
    # ModuleType whose __getattr__ raises -- but the handler only does
    # `import ingest_news`, not attribute access, so that won\'t trigger.
    #
    # The cleanest approach: set sys.modules["ingest_news"] = None.  When
    # Python sees None as a cached module it raises ImportError.  We embed the
    # sentinel in the ImportError by patching builtins.__import__ for the
    # duration of the request.
    # -----------------------------------------------------------------------

    def test_module_import_failure_returns_503(self, monkeypatch):
        """Import failure of news modules -> 503 (module unavailable)."""
        import builtins

        original_import = builtins.__import__

        def _failing_import(name, *args, **kwargs):
            if name in ("ingest_news", "score_news"):
                raise ImportError(f"module not found: {SENTINEL}")
            return original_import(name, *args, **kwargs)

        client = _make_client(monkeypatch)
        # Remove any cached stubs so the import is actually attempted.
        monkeypatch.delitem(sys.modules, "ingest_news", raising=False)
        monkeypatch.delitem(sys.modules, "score_news",  raising=False)
        monkeypatch.setattr(builtins, "__import__", _failing_import)

        resp = client.post(ADMIN_NEWS_PATH, json={}, headers=_auth_headers())

        assert resp.status_code == 503, (
            f"Expected 503 when news modules cannot be imported; "
            f"got {resp.status_code}.\nBody: {resp.text}"
        )

    def test_module_import_failure_no_sentinel_in_body(self, monkeypatch):
        """Import failure: sentinel from ImportError must NOT appear in body."""
        import builtins

        original_import = builtins.__import__

        def _failing_import(name, *args, **kwargs):
            if name in ("ingest_news", "score_news"):
                raise ImportError(f"module not found: {SENTINEL}")
            return original_import(name, *args, **kwargs)

        client = _make_client(monkeypatch)
        monkeypatch.delitem(sys.modules, "ingest_news", raising=False)
        monkeypatch.delitem(sys.modules, "score_news",  raising=False)
        monkeypatch.setattr(builtins, "__import__", _failing_import)

        resp = client.post(ADMIN_NEWS_PATH, json={}, headers=_auth_headers())

        _assert_no_sentinel(resp.text)

    def test_module_import_failure_detail_is_generic(self, monkeypatch):
        """Import failure: detail must equal the fixed generic string."""
        import builtins

        original_import = builtins.__import__

        def _failing_import(name, *args, **kwargs):
            if name in ("ingest_news", "score_news"):
                raise ImportError(f"module not found: {SENTINEL}")
            return original_import(name, *args, **kwargs)

        client = _make_client(monkeypatch)
        monkeypatch.delitem(sys.modules, "ingest_news", raising=False)
        monkeypatch.delitem(sys.modules, "score_news",  raising=False)
        monkeypatch.setattr(builtins, "__import__", _failing_import)

        resp = client.post(ADMIN_NEWS_PATH, json={}, headers=_auth_headers())

        body = resp.json()
        assert body.get("detail") == "News pipeline module unavailable.", (
            f"Expected detail='News pipeline module unavailable.' "
            f"but got {body.get('detail')!r}"
        )
