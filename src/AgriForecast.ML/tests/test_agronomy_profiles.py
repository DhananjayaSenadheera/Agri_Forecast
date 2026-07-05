"""
AgriForecast ML -- R2 Step 2 (CropAgronomyProfiles) coverage-gap tests.

Two invariant classes pinned here (per gate 2.5 QA review):

  (A) Agronomy-frame invariance -- load.load_crops() must keep emitting the
      agronomy columns with the right dtypes, reconstruct PlantingSeason from
      the profile per the binding R2 convention (months populated => Yala/Maha;
      all-NULL months + known GrowthPeriodDays => Year-round; all-NULL + NULL
      gp => None), and (DB-gated) the live post-cut-over distribution must
      match the pinned state recorded in CONTRACTS.md 2026-07-05 R2 Step 2.3
      SHIPPED: 96 rows, 11 gp-non-null, PlantingSeason=='Year-round' for
      exactly those 11, None for the other 85; PlantingSeasonEnc via
      features.py gives {0: gp-known crops, -1: rest} (the training-frame
      {-1: 33555, 0: 13930} distribution derives from this 11-vs-85 split).

  (B) Exclusion-predicate anchor -- pins the CURRENT (2026-07-05,
      hub-adjudicated) forecasting-exclusion rule: a crop is forecastable iff
      GrowthPeriodDays IS NOT NULL, REGARDLESS of IsVerified. An unverified
      profile with known gp is forecastable; a NULL-gp profile (verified or
      not) is not. Step 5.3 flips this to IsVerified-strict once owner-
      approved DOA values land -- see the TODO markers in load.py / features.py
      / build_features.py / serving/predict.py. TestExclusionPredicateAnchor
      is deliberately named/commented so it goes RED the moment Step 5.3 lands
      and must be rewritten (not silently left green) at that point.

Structured like test_merge_asof_dtype.py / test_macro_vintage.py: hermetic
synthetic-frame tests first (no DB, no network), then DB-gated live-pin
invariants via a local _db_or_skip().
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


# ===========================================================================
# (A) Season-reconstruction rule -- hermetic, synthetic CropAgronomyProfiles
#     rows fed through the SAME reconstruction logic load_crops() runs.
# ===========================================================================

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


# ===========================================================================
# (A) load_crops() output shape / dtypes -- hermetic (column contract only;
#     does not require the DB).
# ===========================================================================

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


# ===========================================================================
# (B) Exclusion-predicate anchor -- pins TODAY's behavior; MUST break at Step 5.3.
# ===========================================================================

class TestExclusionPredicateAnchorPreStep5_3:
    """R2 Step 2.3 exclusion predicate (hub-adjudicated, CONTRACTS.md
    2026-07-05): a crop is forecastable iff GrowthPeriodDays IS NOT NULL,
    REGARDLESS of IsVerified.

    *** THIS CLASS IS A RED-THEN-GREEN ANCHOR FOR STEP 5.3. ***
    When Step 5.3 flips the predicate to IsVerified-strict, the first test
    below (unverified-but-known-gp IS forecastable) must FAIL -- that failure
    is the expected signal that the flip landed. At that point rewrite this
    class (do not just delete it) to assert the NEW predicate, and drop this
    docstring's anchor language.
    """

    def _meta_row(self, gp, is_verified):
        return pd.Series({
            "CropCode": "TST000001",
            "CropName": "TestCrop",
            "GrowthPeriodDays": gp,
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

    def test_unverified_but_known_gp_is_forecastable(self):
        """IsVerified=0 (unverified) + GrowthPeriodDays known -> the crop DOES
        get a label / resolves a harvest horizon. Today (pre-Step-5) ALL 96
        profiles are IsVerified=0, so if this test ever fails it means the
        current pooled model has silently lost its only 11 training crops."""
        meta = self._meta_row(gp=60, is_verified=False)
        weather_by_month, rain_clim = self._weather_lookups()
        out = F.build_crop_features("crop-unverified", self._tiny_price_group(),
                                     meta, weather_by_month, rain_clim)
        assert not out["GrowthPeriodDays"].isna().all(), \
            "known GrowthPeriodDays must populate the feature, even if unverified"
        assert out["LabelAvailable"].sum() > 0, \
            "unverified-but-known-gp crop must resolve at least one harvest label -- " \
            "this is the CURRENT (pre-Step-5.3) predicate; if this fails because " \
            "Step 5.3 flipped to IsVerified-strict, that is EXPECTED -- rewrite this " \
            "test class for the new predicate instead of re-greening it silently."

    def test_null_gp_is_not_forecastable_regardless_of_verified_flag(self):
        """NULL GrowthPeriodDays -> excluded from forecasting no matter what
        IsVerified says (there is no growth period to compute a harvest date
        from). This half of the predicate is untouched by the Step 5.3 flip."""
        meta = self._meta_row(gp=None, is_verified=True)
        weather_by_month, rain_clim = self._weather_lookups()
        out = F.build_crop_features("crop-null-gp", self._tiny_price_group(),
                                     meta, weather_by_month, rain_clim)
        assert out["GrowthPeriodDays"].isna().all()
        assert out["LabelAvailable"].sum() == 0, \
            "NULL GrowthPeriodDays must never resolve a harvest label"


# ===========================================================================
# DB-backed invariants (skipped when the live DB is unreachable).
# ===========================================================================

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
        df = _db_or_skip()
        assert len(df) == 96, f"expected 96 crops post-cut-over, got {len(df)}"
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
        df = _db_or_skip()
        gp_known = df["GrowthPeriodDays"].notna()
        assert int(gp_known.sum()) == 11, \
            f"expected 11 gp-non-null crops (pinned 2026-07-05), got {int(gp_known.sum())}"
        # Exactly the 11 gp-known crops are 'Year-round'; the other 85 are None.
        assert (df.loc[gp_known, "PlantingSeason"] == "Year-round").all()
        assert df.loc[~gp_known, "PlantingSeason"].isna().all()

    def test_live_all_profiles_still_unverified_pre_step5(self):
        """Pins the pre-Step-5 state: every live profile is IsVerified=0. This
        test is EXPECTED to start failing once Step 5.3 lands verified values
        -- that is a sign Step 5 shipped, not a regression. Update/remove then."""
        df = _db_or_skip()
        assert (df["IsVerified"] == False).all()  # noqa: E712

    def test_live_planting_season_enc_distribution(self):
        """PlantingSeasonEnc (features.py) must give exactly {0: gp-known
        crops, -1: rest} today -- the {-1: 33555, 0: 13930} training-frame
        distribution (CONTRACTS.md) derives from this per-crop 11-vs-85 split
        fanned out over each crop's price-history row count."""
        df = _db_or_skip()
        enc = df["PlantingSeason"].map(lambda s: F._PLANTING_SEASON_ENC.get(s, -1))
        gp_known = df["GrowthPeriodDays"].notna()
        assert (enc[gp_known] == 0).all()
        assert (enc[~gp_known] == -1).all()
        assert set(enc.unique()) == {0, -1}, \
            "no live crop should encode to 1 (Yala) or 2 (Maha) before Step 5 populates months"
