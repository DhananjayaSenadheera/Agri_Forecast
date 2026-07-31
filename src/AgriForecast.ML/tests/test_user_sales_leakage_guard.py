"""Portfolio Phase 2 (sales log) -- STATIC LEAKAGE GUARD for ``UserSales``.

PRD 3.1: farmer-entered data never trains the model. ``UserSales`` is the one
table holding prices a FARMER typed in, and it is a dead end for them -- nothing
copies a row into PriceObservations/MarketPrices, no view or computed column
exposes PricePerKg to the feature layer, and no module on the Python side may
read it. A farmer's own reported price training the model that then advises that
farmer is a feedback loop dressed up as data.

WHY A SECOND FILE when tests/test_forecast_snapshots.py already scans for
``UserSales`` in its ``_FORBIDDEN_TABLES``: that scan grants an exemption to the
snapshot WRITER (agriforecast_ml/serving/snapshots.py), because ForecastSnapshots
has a sanctioned Python writer. ``UserSales`` has none -- the .NET API owns the
table end to end -- so the correct rule here is stricter than the one over there:
NO file under agriforecast_ml/, and no top-level pipeline script, may name it at
all. Expressing that as its own guard means the exemption list of the other file
can never quietly widen to cover this table too.
"""
from __future__ import annotations

from pathlib import Path

import pytest

ML_ROOT = Path(__file__).resolve().parents[1]

# The table, plus the DTO/field names a loader would have to type to read a price
# out of it. Naming the columns as well as the table catches a raw SELECT that
# aliased the table away.
_FORBIDDEN_TOKENS = ("UserSales", "PricePerKg")

# The named-and-shamed subset: the modules that actually build the training frame.
# A failure pointing at one of these points straight at the law.
_LOADERS = (
    "agriforecast_ml/load.py",
    "agriforecast_ml/features.py",
    "agriforecast_ml/train/dataset.py",
    "agriforecast_ml/store.py",
    "build_features.py",
    "train_model_a.py",
    "qa_features.py",
)


# The smallest number of modules this repo can plausibly have on the model's
# input path. A POSITIVE LOWER BOUND, because "scanned zero files and found zero
# offenders" is the way every absence-scanning guard dies: a renamed package or a
# changed glob would otherwise turn this file green forever.
_MIN_SOURCES_SCANNED = 30


def _all_sources(root: Path = ML_ROOT) -> list[Path]:
    """Every Python module under `root` that can reach the model's inputs: the
    whole package plus the top-level pipeline scripts.

    Scanning the WHOLE tree rather than a hand-listed set is deliberate -- a
    future loader added anywhere is covered without anyone remembering to extend
    a list. `root` is a parameter ONLY so the teeth test below can point the very
    same function at a synthetic tree; production callers never pass it.
    """
    return sorted((root / "agriforecast_ml").rglob("*.py")) + sorted(root.glob("*.py"))


def _offenders(root: Path = ML_ROOT, tokens=_FORBIDDEN_TOKENS) -> list[str]:
    """Files under `root` that name any forbidden token."""
    found = []
    for path in _all_sources(root):
        src = path.read_text(encoding="utf-8")
        found += [f"{path.relative_to(root)}: {t}" for t in tokens if t in src]
    return found


class TestUserSalesIsUnreachableFromPython:
    def test_no_module_names_the_user_sales_table(self):
        """There is NO exemption here, unlike the ForecastSnapshots guard: the
        .NET API owns UserSales end to end, so any Python module naming it is a
        leak by definition."""
        offenders = _offenders()
        assert not offenders, (
            "UserSales holds prices a FARMER typed in. A module on the "
            "feature/training path referencing it feeds the model its own "
            "advice back: " + "; ".join(offenders))

    def test_the_loaders_are_specifically_clean(self):
        """Named-and-shamed subset, so a failure points straight at the law."""
        for rel in _LOADERS:
            src = (ML_ROOT / rel).read_text(encoding="utf-8")
            for token in _FORBIDDEN_TOKENS:
                assert token not in src, f"{rel} must never read {token}"

    def test_the_scan_reaches_a_real_and_substantial_tree(self):
        """Non-vacuity, part 1 -- the POSITIVE LOWER BOUND.

        The test above asserts an ABSENCE, and an absence is also what you get
        from scanning nothing at all. Pin both that the named loaders are in the
        swept set and that the set is not implausibly small, so a `return []`, a
        renamed package or a broken glob is a failure rather than a pass.
        """
        scanned = _all_sources()
        assert len(scanned) >= _MIN_SOURCES_SCANNED, (
            f"only {len(scanned)} modules scanned -- the sweep has lost the tree, "
            "so its clean result means nothing")

        relative = {p.relative_to(ML_ROOT).as_posix() for p in scanned}
        for rel in _LOADERS:
            assert rel in relative, f"{rel} is not being scanned by the guard"

    def test_the_guard_catches_a_real_leak(self, tmp_path):
        """Non-vacuity, part 2 -- THE TEETH, and it must run the real function.

        An earlier version of this test asserted a substring against a string
        literal and never called `_offenders` at all: `return []` at the top of
        the scanner left all five tests green. This one points the SAME function
        at a synthetic tree containing a synthetic loader, so it fails the moment
        the scanner stops scanning.
        """
        package = tmp_path / "agriforecast_ml"
        package.mkdir()
        (package / "clean.py").write_text("import pandas as pd\n", encoding="utf-8")
        (package / "leaky_loader.py").write_text(
            "df = pd.read_sql('SELECT CropId, PricePerKg FROM UserSales', engine)\n",
            encoding="utf-8")
        (tmp_path / "build_features.py").write_text("# nothing to see\n", encoding="utf-8")

        offenders = _offenders(root=tmp_path)

        assert offenders, "the scanner did not catch a module that plainly reads the table"
        assert any("leaky_loader.py" in o for o in offenders)
        assert any(o.endswith("UserSales") for o in offenders)
        assert any(o.endswith("PricePerKg") for o in offenders)
        assert not any("clean.py" in o for o in offenders), (
            "a scanner that flags everything is as useless as one that flags nothing")

    def test_the_forecast_snapshot_guard_still_lists_the_table_too(self):
        """Belt and braces: the older sweep in test_forecast_snapshots.py also
        denies UserSales (with its snapshot-writer exemption). Pinning that here
        means deleting it there is a test failure, not a silent loosening."""
        src = (ML_ROOT / "tests" / "test_forecast_snapshots.py").read_text(encoding="utf-8")
        assert '"UserSales",' in src, (
            "the shared _FORBIDDEN_TABLES sweep must keep denying UserSales")


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(pytest.main([__file__, "-q"]))
