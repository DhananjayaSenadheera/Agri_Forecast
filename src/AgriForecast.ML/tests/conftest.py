"""
Shared fixtures for the AgriForecast ML test suite.

serving_app_with_stubbed_predict
--------------------------------
Several suites (test_admin_auth, test_admin_error_leak,
test_global_exception_handler) need ``agriforecast_ml.serving.app`` imported
with ``agriforecast_ml.serving.predict`` replaced by a lightweight stub so the
registry/DB call at module level in predict.py never executes in a pure
unit-test context.

Historically each of those files installed the stub into sys.modules at
import (collection) time and never removed it, so in a full-suite run the
stub leaked into every later-collected test file: test_phase3 and
test_serving_guard imported MagicMocks instead of the real predict module and
failed, despite passing standalone.  The fixture below scopes the stub to the
requesting test module and restores both sys.modules and the
``agriforecast_ml.serving`` package attributes on teardown, so full runs are
hermetic and standalone runs still never touch the real predict import.
"""
from __future__ import annotations

import importlib
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

# Load src/AgriForecast.ML/.env (AGRI_DB_*, ML_ADMIN_API_KEY) before any test
# runs.  The full suite used to get this as a side effect of serving/app.py
# being imported at collection time by the admin suites; now that those
# imports are fixture-scoped, load it explicitly.  Real environment variables
# always win and this is a no-op when the file is absent (e.g. CI/Docker).
from agriforecast_ml.envfile import load_env_file  # noqa: E402

load_env_file()

_MISSING = object()


def _make_predict_stub() -> ModuleType:
    """Stand-in for agriforecast_ml.serving.predict with just enough surface
    for serving/app.py to import cleanly and serve its public routes."""
    stub = ModuleType("agriforecast_ml.serving.predict")
    stub.model_info = MagicMock(return_value={"version": "v-test", "status": "stub"})
    stub.predict_harvest = MagicMock(
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
    stub.timeline = MagicMock(return_value={"stub": True})
    return stub


@pytest.fixture(scope="module")
def serving_app_with_stubbed_predict():
    """Yield a FastAPI app whose serving.predict is a stub -- hermetically.

    The stub is installed in sys.modules AND as the ``predict`` attribute on
    the ``agriforecast_ml.serving`` package: app.py's ``from . import predict``
    resolves via the package attribute whenever the real module was already
    imported (e.g. by test_phase3 at collection time), so patching sys.modules
    alone is not enough in a full-suite run.  Any cached serving.app module is
    evicted so the import re-binds against the stub.  Teardown restores every
    touched entry, so later test files always see the real modules.
    """
    serving_pkg = importlib.import_module("agriforecast_ml.serving")
    stub = _make_predict_stub()

    saved_modules = {
        "agriforecast_ml.serving.predict": sys.modules.get(
            "agriforecast_ml.serving.predict", _MISSING
        ),
        "agriforecast_ml.serving.app": sys.modules.get(
            "agriforecast_ml.serving.app", _MISSING
        ),
    }
    saved_attrs = {
        "predict": getattr(serving_pkg, "predict", _MISSING),
        "app": getattr(serving_pkg, "app", _MISSING),
    }

    sys.modules["agriforecast_ml.serving.predict"] = stub
    serving_pkg.predict = stub
    sys.modules.pop("agriforecast_ml.serving.app", None)

    try:
        app_module = importlib.import_module("agriforecast_ml.serving.app")
        yield app_module.app
    finally:
        for key, mod in saved_modules.items():
            if mod is _MISSING:
                sys.modules.pop(key, None)
            else:
                sys.modules[key] = mod
        for attr, val in saved_attrs.items():
            if val is _MISSING:
                if hasattr(serving_pkg, attr):
                    delattr(serving_pkg, attr)
            else:
                setattr(serving_pkg, attr, val)
