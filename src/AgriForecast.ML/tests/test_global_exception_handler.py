"""
AgriForecast ML -- Security regression tests for P3 item 12: the global
unhandled-exception backstop (ClickUp 86cahefby).

What item 12 adds
-----------------
Every route already wraps its work and raises HTTPException with a fixed
generic detail. Item 12 adds a global ``@app.exception_handler(Exception)``
safety net so that ANY unhandled exception (a bug in a new route, or an error
raised outside a route's try block) can never fall through to Starlette's
default 500 and leak a traceback / internal file path in a debug/dev config.

Contract proven here
---------------------
1. A route that raises a BARE Exception -> 500 with body exactly
   {"detail": "Internal server error."}, and NONE of the exception message,
   "Traceback", a module path, or ".py" appears anywhere in the body.
2. HTTPException semantics are UNCHANGED: /admin/* with no key still returns
   401 with its existing detail (the backstop must NOT swallow HTTPException).

Design notes
------------
- Same TestClient / sys.modules-predict-stub pattern as test_admin_auth.py and
  test_admin_error_leak.py.
- A throwaway route is registered on the live ``app`` at import time to force
  an unhandled exception through the middleware stack (the existing routes are
  too well-guarded to raise one).
- raise_server_exceptions=False is REQUIRED so the TestClient lets the handler
  render the 500 response instead of re-raising the exception in-process.
"""
from __future__ import annotations

import sys
from pathlib import Path
from types import ModuleType
from unittest.mock import MagicMock

# ---------------------------------------------------------------------------
# Path setup -- allow `import agriforecast_ml` from the ML project root.
# ---------------------------------------------------------------------------
ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

# ---------------------------------------------------------------------------
# Stub agriforecast_ml.serving.predict BEFORE app.py is imported so the
# registry / DB call at module import time in predict.py never executes here.
# (Same pattern as test_admin_auth.py / test_admin_error_leak.py.)
# ---------------------------------------------------------------------------
_predict_stub = ModuleType("agriforecast_ml.serving.predict")
_predict_stub.model_info = MagicMock(return_value={"version": "v-test", "status": "stub"})
_predict_stub.predict_harvest = MagicMock(return_value={})
_predict_stub.timeline = MagicMock(return_value={"stub": True})
sys.modules.setdefault("agriforecast_ml.serving.predict", _predict_stub)

from agriforecast_ml.serving.app import app  # noqa: E402
from starlette.testclient import TestClient    # noqa: E402

# ---------------------------------------------------------------------------
# Constants.
# ---------------------------------------------------------------------------
ADMIN_NEWS_PATH = "/admin/ingest-news"
ENV_VAR = "ML_ADMIN_API_KEY"
CORRECT_KEY = "test-secret-key-item12"

# A sentinel embedded in the raised exception. It represents the internal
# detail (paths, module names, secrets) that must NEVER reach the client via
# an unhandled-exception path.
BOOM_SENTINEL = "BOOM_LEAK_/srv/secret/model.pkl:token=abc123"

# ---------------------------------------------------------------------------
# Register a throwaway route that raises a BARE (non-HTTP) exception, so the
# global backstop is actually exercised. Registered once at import time.
# ---------------------------------------------------------------------------
_BOOM_PATH = "/__test_boom__"


@app.get(_BOOM_PATH)
def _boom_route():  # pragma: no cover - body runs, but coverage tools miss raises
    raise RuntimeError(BOOM_SENTINEL)


def _client() -> TestClient:
    # raise_server_exceptions=False lets the handler render the 500 body
    # instead of the TestClient re-raising the exception in-process.
    return TestClient(app, raise_server_exceptions=False)


# ===========================================================================
# 1 -- Unhandled exception -> generic 500, no leak
# ===========================================================================

class TestGlobalBackstopNoLeak:
    """An unhandled exception must yield a fixed generic 500 with no leak."""

    def test_unhandled_returns_500(self):
        resp = _client().get(_BOOM_PATH)
        assert resp.status_code == 500, (
            f"Unhandled exception must return 500; got {resp.status_code}. "
            f"Body: {resp.text}"
        )

    def test_unhandled_body_is_exact_generic_detail(self):
        resp = _client().get(_BOOM_PATH)
        assert resp.json() == {"detail": "Internal server error."}, (
            f"Backstop must return exactly the generic body; got {resp.text}"
        )

    def test_unhandled_body_leaks_nothing(self):
        body = _client().get(_BOOM_PATH).text
        # None of the exception message or its recognisable fragments.
        for fragment in (
            BOOM_SENTINEL,
            "/srv/secret/model.pkl",
            "token=abc123",
            "BOOM_LEAK",
            "RuntimeError",
        ):
            assert fragment not in body, (
                f"item-12 regression: {fragment!r} leaked into 500 body.\n"
                f"Body: {body}"
            )
        # No traceback / source-path artefacts.
        for artefact in ("Traceback", ".py", "app.py", "site-packages"):
            assert artefact not in body, (
                f"item-12 regression: {artefact!r} leaked into 500 body.\n"
                f"Body: {body}"
            )


# ===========================================================================
# 2 -- HTTPException semantics UNCHANGED (backstop must not swallow them)
# ===========================================================================

class TestHttpExceptionUnchanged:
    """Raising HTTPException in a route/dependency must still route through
    FastAPI's own handler with its intended status + detail."""

    def test_admin_without_key_still_401(self, monkeypatch):
        """/admin/* with no key -> 401 with its existing detail, NOT swallowed
        into the generic 500 by the new backstop."""
        monkeypatch.setenv(ENV_VAR, CORRECT_KEY)
        resp = _client().post(ADMIN_NEWS_PATH, json={})
        assert resp.status_code == 401, (
            f"HTTPException(401) must survive the backstop; got {resp.status_code}. "
            f"Body: {resp.text}"
        )
        assert resp.json().get("detail") == "Invalid or missing API key.", (
            f"401 detail must be unchanged; got {resp.text}"
        )

    def test_admin_misconfig_still_500_with_specific_detail(self, monkeypatch):
        """Fail-closed HTTPException(500) from require_api_key keeps its OWN
        specific detail -- it must NOT be replaced by the generic backstop
        body (proving HTTPException is handled before the Exception backstop)."""
        monkeypatch.delenv(ENV_VAR, raising=False)
        resp = _client().post(
            ADMIN_NEWS_PATH, json={}, headers={"X-API-Key": "anything"}
        )
        assert resp.status_code == 500
        detail = resp.json().get("detail", "")
        assert "misconfiguration" in detail.lower() or "not configured" in detail.lower(), (
            f"HTTPException(500) detail must survive the backstop; got {detail!r}"
        )
        assert detail != "Internal server error.", (
            "Backstop wrongly swallowed a route HTTPException(500)."
        )
