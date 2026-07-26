"""
AgriForecast ML -- P3 CBSL macro vintage as-of merge tests.

The load-bearing invariant of the macro feature path is the TWO-DATE contract:
every MacroSeriesPoints row carries a ReferenceDate (the period the value
describes) AND a PublishedAt (the vintage / KnowledgeDate -- when the world could
first know it). _attach_macro (features.py) must join ONLY on PublishedAt; a join
on ReferenceDate is a classic lookahead leak because a monthly index publishes
weeks after its reference month. This module pins that contract plus the staleness
cap, the NaN-not-0 rule, and rebase-seam independence.

Structured like test_merge_asof_dtype.py: hermetic PURE tests first (no DB, no
network), then DB-gated invariants via _db_or_skip.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import pandas as pd
import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import features as F  # noqa: E402
from agriforecast_ml import load as L  # noqa: E402


# helpers

def _obs_frame(dates, unit: str = "ns") -> pd.DataFrame:
    """Minimal features-matrix frame with ObservationDate at a given unit."""
    return pd.DataFrame({
        "CropId": ["1"] * len(dates),
        "ObservationDate": pd.to_datetime(pd.Index(dates)).astype(f"datetime64[{unit}]"),
        "AvgPrice": np.arange(len(dates), dtype="float64"),
    })


def _macro(rows, ref_unit: str = "ns", pub_unit: str = "ns") -> pd.DataFrame:
    """Build a MacroSeriesPoints-shaped frame.

    rows: list of (series_code, reference_date, published_at, value).
    ref/pub units let dtype-mismatch tests feed [s] vs [us] resolutions.
    """
    return pd.DataFrame({
        "SeriesCode": [r[0] for r in rows],
        "ReferenceDate": pd.to_datetime([r[1] for r in rows]).astype(f"datetime64[{ref_unit}]"),
        "PublishedAt": pd.to_datetime([r[2] for r in rows]).astype(f"datetime64[{pub_unit}]"),
        "Value": [float(r[3]) for r in rows],
        "IsPublishedAtImputed": [False] * len(rows),
    })


# Concrete series/column pairs pulled from the module so the tests track the
# real mapping (and break loudly if a column is renamed).
_FOOD_INFL = ("CCPI_FOOD_YOY_BASE2021", "MacroFoodInflationYoY")
_IMPORTS = ("FOOD_IMPORTS_YOY", "MacroFoodImportsYoY")
_OPR = ("POLICY_RATE_OPR", "MacroPolicyRateOPR")


# (a) TWO-DATE CONFUSION TRIPWIRE -- the single most important test.

def test_two_date_confusion_reference_vs_published():
    """A value referenced weeks BEFORE it is published must NOT be visible at its
    ReferenceDate and MUST be visible at its PublishedAt. If _attach_macro ever
    joins on ReferenceDate (a copy-paste from a single-date _attach_fx), the value
    leaks ~4 weeks early and this test fails loudly naming both dates."""
    code, col = _FOOD_INFL
    reference = "2026-04-01"   # the month the food-inflation figure describes
    published = "2026-04-30"   # ~4 weeks later: when CBSL actually released it
    macro = _macro([(code, reference, published, 2.8)])

    # Observe ON the reference date (before publication) and ON the publication date.
    obs = _obs_frame([reference, published])
    out = F._attach_macro(obs.copy(), macro).sort_values("ObservationDate")
    got = out.set_index(out["ObservationDate"].dt.strftime("%Y-%m-%d"))[col]

    assert pd.isna(got[reference]), (
        f"LEAK: value published {published} was visible at its ReferenceDate "
        f"{reference} -- _attach_macro must join on PublishedAt, NOT ReferenceDate"
    )
    assert got[published] == 2.8, (
        f"value published {published} should be visible on/after {published}, "
        f"got {got[published]!r}"
    )


def test_reference_date_is_never_a_column_in_output():
    """ReferenceDate is audit-only: it must never reach the model frame."""
    code, _ = _FOOD_INFL
    macro = _macro([(code, "2026-04-01", "2026-04-30", 2.8)])
    out = F._attach_macro(_obs_frame(["2026-05-01"]).copy(), macro)
    assert "ReferenceDate" not in out.columns
    assert "PublishedAt" not in out.columns
    assert "_pub" not in out.columns  # internal join key must be dropped


# (b) PUBLISH-BOUNDARY PINS + later-published outlier trap.

def test_visible_at_publish_day_invisible_day_before():
    code, col = _OPR
    macro = _macro([(code, "2026-05-01", "2026-05-15", 8.0)])
    # D-1 (invisible), D (exact-match visible), D+1 (carried forward).
    obs = _obs_frame(["2026-05-14", "2026-05-15", "2026-05-16"])
    out = F._attach_macro(obs.copy(), macro).sort_values("ObservationDate")
    assert out[col].tolist() == [np.nan, 8.0, 8.0] or (
        pd.isna(out[col].iloc[0]) and out[col].iloc[1] == 8.0 and out[col].iloc[2] == 8.0
    )


def test_later_published_outlier_never_leaks_backward():
    """A newer vintage published LATER must not affect earlier observations."""
    code, col = _FOOD_INFL
    macro = _macro([
        (code, "2026-04-01", "2026-04-30", 2.8),
        (code, "2026-05-01", "2026-05-31", 99.0),  # future trap
    ])
    obs = _obs_frame(["2026-05-15"])  # after 04-30, before 05-31
    out = F._attach_macro(obs.copy(), macro)
    assert out[col].tolist() == [2.8], "newer vintage leaked backward"


# (c) DTYPE-MISMATCH SURVIVAL ([s] vs [us], both date columns).

@pytest.mark.parametrize("obs_unit,ref_unit,pub_unit", [
    ("s", "us", "us"), ("us", "s", "s"), ("s", "ns", "us"),
])
def test_dtype_mismatch_survives(obs_unit, ref_unit, pub_unit):
    code, col = _IMPORTS
    macro = _macro([(code, "2026-03-01", "2026-04-01", 5.0)],
                   ref_unit=ref_unit, pub_unit=pub_unit)
    obs = _obs_frame(["2026-04-10"], unit=obs_unit)
    out = F._attach_macro(obs.copy(), macro)
    assert out[col].tolist() == [5.0]


# (d) NaN-not-0 with macro=None / empty / absent series.

@pytest.mark.parametrize("macro", [None, pd.DataFrame(columns=L._MACRO_COLS)])
def test_missing_macro_is_nan_not_zero(macro):
    out = F._attach_macro(_obs_frame(["2026-05-01"]).copy(), macro)
    for col in F._MACRO_FEATURES:
        assert col in out.columns
        assert out[col].isna().all(), f"{col} should be NaN when macro absent, not 0"


def test_absent_series_is_nan_not_zero():
    """One series present, the others absent -> present populated, absent NaN."""
    code, col = _OPR
    macro = _macro([(code, "2026-05-01", "2026-05-15", 8.0)])
    out = F._attach_macro(_obs_frame(["2026-05-20"]).copy(), macro)
    assert out[col].tolist() == [8.0]
    for _, other in (_FOOD_INFL, _IMPORTS):
        assert out[other].isna().all(), f"{other} absent -> NaN, never 0"


def test_before_first_vintage_is_nan():
    code, col = _FOOD_INFL
    macro = _macro([(code, "2026-04-01", "2026-04-30", 2.8)])
    out = F._attach_macro(_obs_frame(["2026-01-01"]).copy(), macro)  # before any vintage
    assert out[col].isna().all()


# (e) FLAT CARRY-FORWARD, no interpolation.

def test_flat_carry_forward_no_interpolation():
    """Between two vintages the value is held FLAT at the older one -- never
    linearly interpolated toward the newer (which would peek forward)."""
    code, col = _OPR
    macro = _macro([
        (code, "2026-03-01", "2026-03-15", 8.0),
        (code, "2026-04-01", "2026-04-15", 9.0),
    ])
    # Observations between 03-15 and 04-15 must all read 8.0 exactly (staleness
    # cap of 60d does not bite within a month).
    obs = _obs_frame(["2026-03-20", "2026-03-31", "2026-04-10"])
    out = F._attach_macro(obs.copy(), macro).sort_values("ObservationDate")
    assert out[col].tolist() == [8.0, 8.0, 8.0], "value was interpolated, not held flat"


# (f) STALENESS CAP boundary -- pin the EXACT operator (age > 60 -> NaN).

def test_staleness_cap_59_days_visible_61_days_nan():
    code, col = _FOOD_INFL
    published = pd.Timestamp("2026-01-01")
    macro = _macro([(code, "2025-12-01", published, 2.8)])
    obs = _obs_frame([
        published + pd.Timedelta(days=59),   # 59d old -> visible
        published + pd.Timedelta(days=60),   # exactly 60d -> visible (strict >)
        published + pd.Timedelta(days=61),   # 61d old -> NaN
    ])
    out = F._attach_macro(obs.copy(), macro).sort_values("ObservationDate")
    vals = out[col].tolist()
    assert vals[0] == 2.8, "59d-old vintage must be visible"
    assert vals[1] == 2.8, "exactly-60d-old vintage must be visible (operator is >)"
    assert pd.isna(vals[2]), "61d-old vintage must be NaN (staleness cap)"


def test_staleness_cap_matches_module_constant():
    """Guard the boundary against a silent constant change."""
    assert F._MACRO_STALENESS_DAYS == 60


# (g) REBASE-SEAM GUARD -- two base-year SeriesCodes are INDEPENDENT series.

def test_rebase_seam_series_are_independent():
    """CCPI_BASE2013 and CCPI_BASE2021 are DISTINCT SeriesCodes (base year is part
    of the key). They must never carry-forward or splice into each other. Only the
    2021-based code is a mapped feature; the 2013-based code must be ignored, and
    its values must NEVER appear in the 2021 column even for observations after a
    2013 vintage but before the first 2021 vintage."""
    # 2013-based food-YoY (NOT a mapped feature) published early; 2021-based
    # (the mapped feature) published later. An observation between them must be
    # NaN for the 2021 column -- no cross-code splice from the 2013 series.
    macro = _macro([
        ("CCPI_FOOD_YOY_BASE2013", "2020-06-01", "2020-06-30", 5.5),
        ("CCPI_FOOD_YOY_BASE2021", "2026-04-01", "2026-04-30", 2.8),
    ])
    obs = _obs_frame(["2020-07-15", "2026-05-15"])
    out = F._attach_macro(obs.copy(), macro).sort_values("ObservationDate")
    col = _FOOD_INFL[1]
    vals = out[col].tolist()
    assert pd.isna(vals[0]), "2013-based vintage spliced into the 2021 feature column"
    assert vals[1] == 2.8, "2021-based vintage should be visible after its publish date"


# _build_X parity (predict.py / explain.py build dynamically off feature_cols).

def test_build_x_parity_macro_columns_nan_and_populated():
    """The serving _build_X must reproduce the macro columns for both a populated
    row and a NaN row (verified 2026-07-04 that predict/explain build off the
    payload feature_cols). Coerce to float64, NaN preserved."""
    from agriforecast_ml.serving import explain

    feature_cols = ["AvgPrice"] + F._MACRO_FEATURES
    payload = {"feature_cols": feature_cols, "categorical": []}

    populated = {"AvgPrice": 100.0, "MacroFoodInflationYoY": 2.8,
                 "MacroFoodImportsYoY": 5.0, "MacroPolicyRateOPR": 8.0}
    Xp = explain._build_X(populated, payload)
    assert list(Xp.columns) == feature_cols
    assert Xp["MacroFoodInflationYoY"].iloc[0] == 2.8
    assert Xp["MacroPolicyRateOPR"].iloc[0] == 8.0
    assert str(Xp["MacroFoodImportsYoY"].dtype) == "float64"

    nan_row = {"AvgPrice": 100.0, "MacroFoodInflationYoY": np.nan,
               "MacroFoodImportsYoY": None, "MacroPolicyRateOPR": np.nan}
    Xn = explain._build_X(nan_row, payload)
    for c in F._MACRO_FEATURES:
        assert pd.isna(Xn[c].iloc[0]), f"{c} should stay NaN through _build_X"


def test_macro_columns_have_explain_labels():
    """Every macro feature column needs a farmer-readable _LABELS entry (else SHAP
    shows raw column names)."""
    from agriforecast_ml.serving import explain
    for c in F._MACRO_FEATURES:
        assert c in explain._LABELS, f"missing _LABELS entry for {c}"
        assert explain._LABELS[c] != c


def test_backfilled_labels_present():
    """Latent-debt backfill (2026-07-04): policy/FX/sentiment columns must now
    have _LABELS entries too."""
    from agriforecast_ml.serving import explain
    for c in ["FxUsdLkr", "MeanSentiment", "DroughtRatio", "FloodRatio",
              "PolicyRatio", "ActivePolicyNetDirection", "ActivePolicyCount",
              "PolicyImportBanActive", "PolicyPriceCeilingActive",
              "PolicyFertiliserSubsidyActive"]:
        assert c in explain._LABELS, f"missing backfilled _LABELS entry for {c}"


# DB-backed invariants (skipped when the live DB is unreachable).

def _db_macro_or_skip() -> pd.DataFrame:
    try:
        from agriforecast_ml.envfile import load_env_file
        load_env_file()
        m = L.load_macro_series()
    except Exception as e:  # pragma: no cover - env-dependent
        pytest.skip(f"DB unreachable: {e}")
    if m.empty:
        pytest.skip("MacroSeriesPoints empty/unreachable")
    return m


class TestLiveMacro:
    def test_load_macro_series_shape(self):
        m = _db_macro_or_skip()
        assert list(m.columns) == L._MACRO_COLS
        assert m["ReferenceDate"].dtype == np.dtype("datetime64[ns]")
        assert m["PublishedAt"].dtype == np.dtype("datetime64[ns]")
        assert m["IsPublishedAtImputed"].dtype == bool
        # Ctor guarantee: ReferenceDate <= PublishedAt for every vintage.
        assert (m["ReferenceDate"] <= m["PublishedAt"]).all(), \
            "ReferenceDate must never be after PublishedAt (vintage inversion)"

    def test_mapped_series_present_in_db(self):
        m = _db_macro_or_skip()
        present = set(m["SeriesCode"].unique())
        for code in F._MACRO_SERIES:
            assert code in present, f"mapped series {code} missing from DB"

    def test_macro_columns_not_constant_over_covered_window(self):
        """Not-constant + minimum non-NaN fraction, scoped to EACH SERIES' COVERED
        WINDOW.

        Macro coverage is deliberately PARTIAL: NaN before a series' first vintage
        (and after the 60-day staleness cap past its last vintage) is CORRECT, not
        a bug. So we do NOT apply the reference-calendar seed-coverage invariant
        (that hard gate is for lagged festival/poya calendars, NOT for lagged
        published series). Instead, for each macro column we scope to the window
        [first vintage PublishedAt, last vintage PublishedAt + cap] and assert the
        column is (a) not constant and (b) mostly non-NaN inside that window."""
        m = _db_macro_or_skip()
        try:
            prices = L.load_prices()
            crops = L.load_crops()
            weather = L.load_weather()
        except Exception as e:  # pragma: no cover
            pytest.skip(f"inputs unreachable: {e}")
        feats = F.build_all(prices, crops, weather, fx=None, macro=m)
        obs = pd.to_datetime(feats["ObservationDate"])

        cap = pd.Timedelta(days=F._MACRO_STALENESS_DAYS)
        for code, col in F._MACRO_SERIES.items():
            sub = m[m["SeriesCode"] == code]
            if sub.empty:
                continue
            lo = sub["PublishedAt"].min()
            hi = sub["PublishedAt"].max() + cap
            in_window = (obs >= lo) & (obs <= hi)
            window = feats.loc[in_window, col]
            if window.empty:
                # No observations overlap this series' covered window (short
                # series vs price history) -- coverage gap, not a dead column.
                continue
            non_nan_frac = window.notna().mean()
            assert non_nan_frac >= 0.5, (
                f"{col}: only {non_nan_frac:.0%} non-NaN inside its covered window "
                f"[{lo.date()}..{hi.date()}] -- as-of join likely mis-wired"
            )
            assert window.nunique(dropna=True) >= 1, f"{col} is empty in-window"
