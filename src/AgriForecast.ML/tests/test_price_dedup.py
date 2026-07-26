"""Duplicate same-(crop, source, date) price-row handling (fix/dup-price-rows).

Regression coverage for the 2026-07-17 production crash
``ValueError: cannot reindex on an axis with duplicate labels`` at
``features.build_crop_features`` (``daily = g.reindex(full)``).

Root cause: the step-8.2 Passion crop-merge made one crop (FRT000019) inherit
BOTH Dambulla-DEC product listings (ExternalProductId 43 & 76), yielding 2 rows
per (crop, source, date) -- BY DESIGN in the DB. The pipeline's one-row-per-
(crop, date) assumption is what became robust:

  * PRIMARY fix -- ``load.load_prices`` averages MinPrice/MaxPrice per
    (CropId, Source, PriceDate). Generic (not a Passion special-case) and a
    strict no-op for already-unique data. Grouping is WITHIN a source so the
    HARTI/DEC splice precedence is untouched.
  * DEFENSE-IN-DEPTH -- ``features.build_crop_features`` collapses any duplicate
    PriceDate by mean before reindex, so a future splice regression degrades
    gracefully instead of crashing.

The pure-unit tests monkeypatch the SQL read so they run with NO database. The
live-DB tests (audit + Passion pin) skip cleanly when the DB is unreachable.
"""
from __future__ import annotations

import numpy as np
import pandas as pd
import pytest

from agriforecast_ml import features
import agriforecast_ml.load as L


# Pure-unit tests: monkeypatch the DB read so load_prices runs with no DB.
_RAW_COLS = ["CropId", "CropCode", "CropName", "Source",
             "PriceDate", "MinPrice", "MaxPrice"]
_OUT_COLS = ["CropId", "CropCode", "CropName", "PriceDate",
             "MinPrice", "MaxPrice", "AvgPrice"]


def _run_load_prices(monkeypatch, raw: pd.DataFrame) -> pd.DataFrame:
    """Invoke load.load_prices() against a crafted 'SQL' frame (no DB)."""
    monkeypatch.setattr(L, "get_engine", lambda: None)
    monkeypatch.setattr(L.pd, "read_sql", lambda *a, **k: raw.copy())
    return L.load_prices()


def _raw(rows: list[dict]) -> pd.DataFrame:
    return pd.DataFrame(rows, columns=_RAW_COLS).assign(
        PriceDate=lambda d: pd.to_datetime(d["PriceDate"]),
        MinPrice=lambda d: d["MinPrice"].astype(float),
        MaxPrice=lambda d: d["MaxPrice"].astype(float),
    )


def test_load_prices_collapses_same_source_date_to_mean(monkeypatch):
    """Two rows for the same (crop, source, date) -> one row = mean of Min/Max."""
    raw = _raw([
        dict(CropId="c1", CropCode="X", CropName="Xc", Source="DAMBULLA_DEC",
             PriceDate="2025-01-01", MinPrice=100, MaxPrice=120),
        dict(CropId="c1", CropCode="X", CropName="Xc", Source="DAMBULLA_DEC",
             PriceDate="2025-01-01", MinPrice=200, MaxPrice=240),
        dict(CropId="c1", CropCode="X", CropName="Xc", Source="DAMBULLA_DEC",
             PriceDate="2025-01-02", MinPrice=50, MaxPrice=70),
    ])
    out = _run_load_prices(monkeypatch, raw)

    assert list(out.columns) == _OUT_COLS       # Source dropped, schema unchanged
    assert "Source" not in out.columns
    d1 = out[out["PriceDate"] == pd.Timestamp("2025-01-01")]
    assert len(d1) == 1
    assert d1["MinPrice"].iloc[0] == 150.0       # mean(100, 200)
    assert d1["MaxPrice"].iloc[0] == 180.0       # mean(120, 240)
    assert d1["AvgPrice"].iloc[0] == 165.0       # (150 + 180) / 2
    # Unique (crop, date) afterwards -> reindex downstream cannot raise.
    assert not out.duplicated(["CropId", "PriceDate"]).any()


def test_load_prices_noop_for_unique_data(monkeypatch):
    """Already-unique (crop, source, date) data passes through unchanged."""
    raw = _raw([
        dict(CropId="c1", CropCode="X", CropName="Xc", Source="HARTI",
             PriceDate="2025-01-01", MinPrice=100, MaxPrice=120),
        dict(CropId="c1", CropCode="X", CropName="Xc", Source="HARTI",
             PriceDate="2025-01-02", MinPrice=110, MaxPrice=130),
        dict(CropId="c2", CropCode="Y", CropName="Yc", Source="HARTI",
             PriceDate="2025-01-01", MinPrice=10, MaxPrice=30),
    ])
    out = _run_load_prices(monkeypatch, raw)

    assert len(out) == 3
    exp = raw.drop(columns=["Source"]).copy()
    # load_prices pins PriceDate to the canonical [ns] unit (leakage-inert dtype
    # normalization); mirror it so the no-op comparison is value+dtype exact.
    exp["PriceDate"] = exp["PriceDate"].astype("datetime64[ns]")
    exp["AvgPrice"] = (exp["MinPrice"] + exp["MaxPrice"]) / 2.0
    key = ["CropId", "PriceDate"]
    got = out.sort_values(key).reset_index(drop=True)
    exp = exp.sort_values(key).reset_index(drop=True)[_OUT_COLS]
    pd.testing.assert_frame_equal(got, exp)


def test_load_prices_never_blends_across_sources(monkeypatch):
    """Same (crop, date) in two SOURCES stays two rows -- HARTI/DEC precedence is
    grouped WITHIN a source, never merged. (The 2025-05-05 splice, not this
    dedup, is what removes real cross-source date overlaps.)"""
    raw = _raw([
        dict(CropId="c1", CropCode="X", CropName="Xc", Source="HARTI",
             PriceDate="2025-01-01", MinPrice=100, MaxPrice=100),
        dict(CropId="c1", CropCode="X", CropName="Xc", Source="DAMBULLA_DEC",
             PriceDate="2025-01-01", MinPrice=200, MaxPrice=200),
    ])
    out = _run_load_prices(monkeypatch, raw)
    d = out[out["PriceDate"] == pd.Timestamp("2025-01-01")]
    assert len(d) == 2                            # NOT merged into one
    assert set(d["AvgPrice"]) == {100.0, 200.0}   # both source values intact


def test_load_prices_dedup_is_order_independent(monkeypatch):
    """Row order in the source frame must not change the deduped output."""
    rows = [
        dict(CropId="c1", CropCode="X", CropName="Xc", Source="DAMBULLA_DEC",
             PriceDate="2025-01-01", MinPrice=200, MaxPrice=240),
        dict(CropId="c1", CropCode="X", CropName="Xc", Source="DAMBULLA_DEC",
             PriceDate="2025-01-01", MinPrice=100, MaxPrice=120),
        dict(CropId="c2", CropCode="Y", CropName="Yc", Source="HARTI",
             PriceDate="2025-01-02", MinPrice=10, MaxPrice=30),
    ]
    a = _run_load_prices(monkeypatch, _raw(rows))
    b = _run_load_prices(monkeypatch, _raw(list(reversed(rows))))
    pd.testing.assert_frame_equal(a, b)


def test_load_prices_dedup_is_nan_safe(monkeypatch):
    """A NaN in one duplicate row is skipped by the mean (never NaN-poisons)."""
    raw = _raw([
        dict(CropId="c1", CropCode="X", CropName="Xc", Source="DAMBULLA_DEC",
             PriceDate="2025-01-01", MinPrice=100, MaxPrice=120),
        dict(CropId="c1", CropCode="X", CropName="Xc", Source="DAMBULLA_DEC",
             PriceDate="2025-01-01", MinPrice=np.nan, MaxPrice=140),
    ])
    out = _run_load_prices(monkeypatch, raw)
    d = out[out["PriceDate"] == pd.Timestamp("2025-01-01")]
    assert len(d) == 1
    assert d["MinPrice"].iloc[0] == 100.0         # NaN skipped -> mean(100)
    assert d["MaxPrice"].iloc[0] == 130.0         # mean(120, 140)


# Defense-in-depth: features.build_crop_features must survive dup PriceDates.
def _meta() -> pd.Series:
    return pd.Series({
        "CropName": "Xc", "CropCode": "X", "GrowthPeriodDays": 90,
        "IsVerified": True, "PlantingSeason": "Year-round", "HarvestWindowDays": 14,
    })


def test_build_crop_features_guard_collapses_duplicate_dates():
    """A group carrying duplicate PriceDates must NOT raise on reindex; the
    duplicate day collapses to the mean price (belt-and-braces for a splice
    regression that slips past the loader)."""
    grp = pd.DataFrame({
        "CropId": ["c1"] * 4, "CropCode": ["X"] * 4, "CropName": ["Xc"] * 4,
        "PriceDate": pd.to_datetime(
            ["2025-01-01", "2025-01-01", "2025-01-02", "2025-01-03"]),
        "MinPrice": [100.0, 200.0, 50.0, 60.0],
        "MaxPrice": [120.0, 240.0, 70.0, 80.0],
    })
    grp["AvgPrice"] = (grp["MinPrice"] + grp["MaxPrice"]) / 2.0

    out = features.build_crop_features("c1", grp, _meta(), {}, {})

    assert out["ObservationDate"].is_unique
    row = out[out["ObservationDate"] == pd.Timestamp("2025-01-01")]
    assert len(row) == 1
    assert row["AvgPrice"].iloc[0] == 165.0       # mean(110, 220)


def test_build_crop_features_guard_is_noop_when_unique():
    """With unique PriceDates the guard is inert -- output is identical to a
    build over the same frame (no accidental aggregation)."""
    grp = pd.DataFrame({
        "CropId": ["c1"] * 3, "CropCode": ["X"] * 3, "CropName": ["Xc"] * 3,
        "PriceDate": pd.to_datetime(["2025-01-01", "2025-01-02", "2025-01-03"]),
        "MinPrice": [100.0, 110.0, 120.0],
        "MaxPrice": [120.0, 130.0, 140.0],
    })
    grp["AvgPrice"] = (grp["MinPrice"] + grp["MaxPrice"]) / 2.0
    out = features.build_crop_features("c1", grp, _meta(), {}, {})
    assert list(out["AvgPrice"]) == [110.0, 120.0, 130.0]
    assert out["ObservationDate"].is_unique


# Live-DB pins: real load_prices output + the Passion (FRT000019) case.
def _live_prices():
    """load_prices() from the live DB, or skip when the DB is unreachable."""
    try:
        return L.load_prices()
    except Exception as exc:  # pragma: no cover - env-dependent
        pytest.skip(f"live DB unavailable: {exc}")


def test_live_load_prices_has_unique_crop_date():
    """The whole blended daily loader must emit exactly one row per (crop, date)
    so the per-crop reindex in build_crop_features cannot crash for ANY crop."""
    df = _live_prices()
    dup = df.duplicated(["CropId", "PriceDate"]).sum()
    assert dup == 0, f"{dup} duplicate (CropId, PriceDate) rows remain"


def test_live_passion_dedup_matches_raw_mean():
    """Passion (FRT000019) carries 2 Dambulla-DEC rows/date BY DESIGN; the loader
    must serve one row/date equal to the mean of the two raw rows."""
    from agriforecast_ml.db import get_engine

    df = _live_prices()
    pas = df[df["CropCode"] == "FRT000019"]
    if pas.empty:
        pytest.skip("Passion (FRT000019) not present in this DB")
    # one row per date
    assert not pas.duplicated(["CropId", "PriceDate"]).any()

    # NOTE: not scoped by Source — valid only while Passion is DEC-only. If
    # Passion ever gains a HARTI history, add Source to this query + groupby.
    raw = pd.read_sql(
        "SELECT mp.PriceDate, mp.MinPrice, mp.MaxPrice "
        "FROM MarketPrices mp JOIN Crops c ON c.Id = mp.CropId "
        "WHERE c.CropCode = 'FRT000019' AND mp.MaxPrice > 0",
        get_engine(),
    )
    raw["PriceDate"] = pd.to_datetime(raw["PriceDate"])
    raw_mean = raw.groupby("PriceDate")[["MinPrice", "MaxPrice"]].mean()
    # feed emits both ids -> at least some dates have 2 raw rows
    assert len(raw) > len(raw_mean), "expected duplicated raw Passion rows"

    got = pas.set_index("PriceDate")[["MinPrice", "MaxPrice"]].sort_index()
    exp = raw_mean.reindex(got.index)
    assert np.allclose(got.values, exp.values), "Passion daily != raw two-row mean"
