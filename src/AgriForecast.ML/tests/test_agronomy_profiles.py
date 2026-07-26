"""CropAgronomyProfiles coverage-gap tests.

Two invariant classes are pinned here.

(A) Agronomy-frame invariance: load.load_crops() must keep emitting the agronomy columns
    with the right dtypes and reconstruct PlantingSeason from the profile - months
    populated means Yala or Maha, all-NULL months with a known GrowthPeriodDays means
    Year-round, all-NULL with no gp means None. The DB-gated tests pin the live
    distribution.

(B) Exclusion predicate: a crop is forecastable only if its profile is IsVerified=1 AND
    GrowthPeriodDays is not NULL. An unverified profile is excluded even when it carries a
    legacy gp. The shared gate is load.resolve_forecast_gp, applied by both the feature
    build and serving.

Hermetic synthetic-frame tests come first, then DB-gated live pins that skip when the DB
is unreachable.
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


# (A) Season-reconstruction rule -- hermetic, synthetic CropAgronomyProfiles
#     rows fed through the SAME reconstruction logic load_crops() runs.

def _reconstruct_season(df: pd.DataFrame) -> pd.Series:
    """Mirrors load.load_crops()'s season-reconstruction block exactly (kept as
    a standalone helper here so the test can feed synthetic profile rows
    without a live DB). If load_crops() ever changes this logic without a
    matching edit here, the DB-gated tests below will catch drift on the real
    frame; this hermetic copy pins the RULE in isolation."""
    yala_cols = ["YalaPlantingStartMonth", "YalaPlantingEndMonth"]
    maha_cols = ["MahaPlantingStartMonth", "MahaPlantingEndMonth"]
    has_yala = df[yala_cols].notna().any(axis=1)
    has_maha = df[maha_cols].notna().any(axis=1)
    season = pd.Series([None] * len(df), index=df.index, dtype=object)
    season = season.mask(df["GrowthPeriodDays"].notna() & ~has_yala & ~has_maha,
                         "Year-round")
    season = season.mask(has_maha, "Maha")
    season = season.mask(has_yala, "Yala")
    return season


def _profile_row(gp=None, yala_start=None, yala_end=None, maha_start=None, maha_end=None):
    return {
        "GrowthPeriodDays": gp,
        "YalaPlantingStartMonth": yala_start,
        "YalaPlantingEndMonth": yala_end,
        "MahaPlantingStartMonth": maha_start,
        "MahaPlantingEndMonth": maha_end,
    }


def test_season_reconstruction_months_populated_gives_yala_or_maha():
    df = pd.DataFrame([
        _profile_row(gp=60, yala_start=3, yala_end=4),   # Yala months set
        _profile_row(gp=60, maha_start=10, maha_end=11),  # Maha months set
    ])
    season = _reconstruct_season(df)
    assert season.tolist() == ["Yala", "Maha"]


def test_season_reconstruction_all_null_months_with_gp_is_year_round():
    df = pd.DataFrame([_profile_row(gp=90)])  # gp known, all month cols NULL
    season = _reconstruct_season(df)
    assert season.tolist() == ["Year-round"]


def test_season_reconstruction_all_null_months_no_gp_is_none():
    df = pd.DataFrame([_profile_row(gp=None)])  # nothing known
    season = _reconstruct_season(df)
    assert season.tolist() == [None]


def test_season_reconstruction_yala_wins_if_both_yala_and_maha_set():
    """Documents the tie-break in load.py: has_yala mask is applied AFTER
    has_maha, so if a row somehow has both sets of months populated, Yala wins.
    Pins the current behavior; not expected to occur pre-Step-5 (all months are
    NULL today) but guards a future data-entry edge case."""
    df = pd.DataFrame([_profile_row(gp=60, yala_start=3, yala_end=4,
                                     maha_start=10, maha_end=11)])
    season = _reconstruct_season(df)
    assert season.tolist() == ["Yala"]


# (A) load_crops() output shape / dtypes -- hermetic (column contract only;
#     does not require the DB).

def test_load_crops_sql_selects_profile_columns():
    """Guards the SELECT list itself: if a column is dropped from the JOIN,
    downstream consumers silently lose agronomy data with no error. Static
    check on the module source (no DB call) so it runs everywhere."""
    import inspect
    src = inspect.getsource(L.load_crops)
    for col in ("GrowthPeriodDays", "HarvestWindowDays",
                "YalaPlantingStartMonth", "YalaPlantingEndMonth",
                "MahaPlantingStartMonth", "MahaPlantingEndMonth",
                "IsPerennial", "IsVerified", "CropAgronomyProfiles"):
        assert col in src, f"load_crops() SQL no longer references {col}"
    assert "LEFT JOIN" in src, "join must stay LEFT so a profile-less crop still surfaces (NULL agronomy -> excluded downstream, not silently dropped)"


# (B) Exclusion predicate -- R2 Step 5.3 IsVerified-STRICT (rewritten from the
#     pre-flip gp-only anchor, which necessarily went RED when the flip landed).

class TestExclusionPredicateIsVerifiedStrict:
    """A crop is forecastable only if its agronomy profile is IsVerified=1 AND has a usable
    GrowthPeriodDays.

    An unverified profile is excluded exactly like a NULL-gp crop, even when it still carries
    a legacy growth period.
    """

    def _meta_row(self, gp, is_verified):
        # IsVerified is now load-bearing: it flows into the exclusion predicate
        # via meta.get("IsVerified") in build_crop_features. The pre-flip helper
        # silently dropped this key, which is precisely why the old anchor breaks.
        return pd.Series({
            "CropCode": "TST000001",
            "CropName": "TestCrop",
            "GrowthPeriodDays": gp,
            "IsVerified": is_verified,
            "PlantingSeason": "Year-round" if gp is not None else None,
            "HarvestWindowDays": np.nan,
        })

    def _weather_lookups(self):
        return {}, {}

    def _tiny_price_group(self):
        dates = pd.date_range("2024-01-01", periods=120, freq="D")
        return pd.DataFrame({
            "PriceDate": dates,
            "AvgPrice": np.linspace(100, 120, len(dates)),
            "MinPrice": np.linspace(95, 115, len(dates)),
            "MaxPrice": np.linspace(105, 125, len(dates)),
        })

    def test_verified_with_known_gp_is_forecastable(self):
        """IsVerified=1 + GrowthPeriodDays known -> the crop DOES resolve a
        harvest horizon and at least one label. This is the ONLY combination
        that forecasts under the Step 5.3 predicate; the owner-verified model
        crops must land here."""
        meta = self._meta_row(gp=60, is_verified=True)
        weather_by_month, rain_clim = self._weather_lookups()
        out = F.build_crop_features("crop-verified", self._tiny_price_group(),
                                     meta, weather_by_month, rain_clim)
        assert not out["GrowthPeriodDays"].isna().all(), \
            "verified crop with a known GrowthPeriodDays must populate the feature"
        assert out["LabelAvailable"].sum() > 0, \
            "verified-with-known-gp crop must resolve at least one harvest label"

    def test_unverified_but_known_gp_is_now_excluded(self):
        """IsVerified=0 with a legacy GrowthPeriodDays is NOT forecastable: the gp is not honoured,
        so there is no harvest horizon and no label, and the crop drops out of the trainable set
        exactly as a NULL-gp crop does.
        """
        meta = self._meta_row(gp=60, is_verified=False)
        weather_by_month, rain_clim = self._weather_lookups()
        out = F.build_crop_features("crop-unverified", self._tiny_price_group(),
                                     meta, weather_by_month, rain_clim)
        assert out["GrowthPeriodDays"].isna().all(), \
            "unverified profile's GrowthPeriodDays must NOT be honored (gp=None)"
        assert out["LabelAvailable"].sum() == 0, \
            "unverified-but-gp crop must resolve ZERO labels under IsVerified-strict"

    def test_null_gp_is_not_forecastable_regardless_of_verified_flag(self):
        """NULL GrowthPeriodDays -> excluded no matter what IsVerified says
        (there is no growth period to compute a harvest date from). This half of
        the predicate is untouched by the Step 5.3 flip; verified=True here to
        prove the gp-NULL exclusion is independent of the verified flag."""
        meta = self._meta_row(gp=None, is_verified=True)
        weather_by_month, rain_clim = self._weather_lookups()
        out = F.build_crop_features("crop-null-gp", self._tiny_price_group(),
                                     meta, weather_by_month, rain_clim)
        assert out["GrowthPeriodDays"].isna().all()
        assert out["LabelAvailable"].sum() == 0, \
            "NULL GrowthPeriodDays must never resolve a harvest label"

    def test_resolve_forecast_gp_predicate_truth_table(self):
        """Unit-pins the shared gate itself (load.resolve_forecast_gp) — the
        single source of truth both train and serve route through. Only
        (verified AND gp-known) yields an int horizon; every other cell is None."""
        assert L.resolve_forecast_gp(True, 60) == 60
        assert L.resolve_forecast_gp(False, 60) is None   # the flip
        assert L.resolve_forecast_gp(True, None) is None
        assert L.resolve_forecast_gp(False, None) is None
        assert L.resolve_forecast_gp(None, 60) is None    # profile-less LEFT JOIN
        assert L.resolve_forecast_gp(1, 90) == 90         # int truthy encoding
        assert L.resolve_forecast_gp(0, 90) is None
        # non-positive gp is not a usable horizon (degenerate shift) -> excluded
        assert L.resolve_forecast_gp(True, 0) is None
        assert L.resolve_forecast_gp(True, -5) is None


# DB-backed invariants (skipped when the live DB is unreachable).

def _db_or_skip() -> pd.DataFrame:
    try:
        from agriforecast_ml.envfile import load_env_file
        load_env_file()
        df = L.load_crops()
    except Exception as e:  # pragma: no cover - env-dependent
        pytest.skip(f"DB unreachable: {e}")
    if df.empty:
        pytest.skip("Crops/CropAgronomyProfiles empty/unreachable")
    return df


class TestLiveAgronomyProfilePin:
    """Pins the post-cut-over live distribution recorded in CONTRACTS.md
    2026-07-05 R2 Step 2.1/2.3 SHIPPED. A change here without an accompanying
    CONTRACTS.md update / deliberate data change is a regression."""

    def test_load_crops_shape_and_dtypes(self):
        """Pinned at 96 crops: 95 after the Passion duplicate merge, plus Garlic VEG000071."""
        df = _db_or_skip()
        assert len(df) == 96, f"expected 96 crops post-Garlic-fix, got {len(df)}"
        for col in ("CropId", "CropCode", "CropName", "GrowthPeriodDays",
                    "HarvestWindowDays", "YalaPlantingStartMonth",
                    "YalaPlantingEndMonth", "MahaPlantingStartMonth",
                    "MahaPlantingEndMonth", "IsPerennial", "IsVerified",
                    "PlantingSeason"):
            assert col in df.columns, f"load_crops() missing column {col}"
        assert df["CropId"].map(type).eq(str).all()
        assert df["GrowthPeriodDays"].dtype.kind == "f"
        assert df["IsPerennial"].dtype == bool
        assert df["IsVerified"].dtype == bool

    def test_live_gp_non_null_count_and_season_split(self):
        """Pinned to the live state: 83 crops with a non-NULL growth period, split Yala 54 /
        Year-round 26 / Maha 3.
        """
        df = _db_or_skip()
        gp_known = df["GrowthPeriodDays"].notna()
        assert int(gp_known.sum()) == 83, \
            f"expected 83 gp-non-null crops (post-Passion-merge), got {int(gp_known.sum())}"
        # gp-known crops now split across all three PlantingSeason values
        # (Yala/Year-round/Maha), not exclusively 'Yala' as under Phase 1.
        season_counts = df.loc[gp_known, "PlantingSeason"].value_counts()
        assert season_counts.to_dict() == {"Yala": 54, "Year-round": 26, "Maha": 3}, \
            f"expected Yala=54/Year-round=26/Maha=3 split, got {season_counts.to_dict()}"
        assert df.loc[~gp_known, "PlantingSeason"].isna().all()

    def test_live_verified_set_equals_forecastable_set(self):
        """The forecastable (gp-known) set is a strict SUBSET of the verified set, not equal to it.

        Ten verified crops are perennials with no discrete GrowthPeriodDays, which is agronomy
        rather than a data gap, and two crops remain unverified.
        """
        df = _db_or_skip()
        verified = df["IsVerified"] == True   # noqa: E712
        gp_known = df["GrowthPeriodDays"].notna()
        assert int(verified.sum()) == 93, \
            f"expected 93 verified profiles (post-Passion-merge), got {int(verified.sum())}"
        # forecastable (gp-known) set must be a SUBSET of verified -- never
        # gp-known-but-unverified (that would defeat the IsVerified-strict gate).
        assert (gp_known <= verified).all(), \
            "IsVerified-strict: every gp-known row must also be verified"
        # the verified-but-gp-null rows must be exactly the 10 known perennials.
        verified_no_gp = df[verified & ~gp_known]
        assert len(verified_no_gp) == 10, \
            f"expected 10 verified-but-gp-null perennial rows, got {len(verified_no_gp)}"
        assert verified_no_gp["IsPerennial"].all(), \
            "every verified-but-gp-null row must be a perennial (agronomically expected gp=NULL)"

    def test_live_planting_season_enc_distribution(self):
        """PlantingSeasonEnc spans all three encoded values on the gp-known set (Yala 1, Year-round 0,
        Maha 2). The 12 gp-null rows encode to -1, since PlantingSeason is None for all of them.
        """
        df = _db_or_skip()
        enc = df["PlantingSeason"].map(lambda s: F._PLANTING_SEASON_ENC.get(s, -1))
        gp_known = df["GrowthPeriodDays"].notna()
        enc_counts = enc[gp_known].value_counts().to_dict()
        assert enc_counts == {1: 54, 0: 26, 2: 3}, \
            f"expected Yala=1(54)/Year-round=0(26)/Maha=2(3), got {enc_counts}"
        assert (enc[~gp_known] == -1).all()
        assert set(enc.unique()) == {1, 0, 2, -1}, \
            f"expected encoded values {{1,0,2,-1}}, got {set(enc.unique())}"
