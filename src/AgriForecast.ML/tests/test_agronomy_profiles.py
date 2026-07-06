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

  (B) Exclusion predicate -- pins the R2 Step 5.3 IsVerified-STRICT rule
      (REWRITTEN 2026-07-06 from the pre-flip anchor): a crop is forecastable
      iff its profile is IsVerified=1 AND GrowthPeriodDays IS NOT NULL. An
      unverified profile — even with a legacy gp — is EXCLUDED (no served
      horizon, crop-mean fallback); a NULL-gp profile is excluded regardless of
      the verified flag. The shared gate is load.resolve_forecast_gp, applied by
      features.build_crop_features (train) and serving/predict._crop_meta
      (serve). The predecessor class TestExclusionPredicateAnchorPreStep5_3
      asserted the OLD gp-only predicate and was the deliberate RED-then-rewrite
      anchor for this flip; TestExclusionPredicateIsVerifiedStrict below encodes
      the new contract.

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
# (B) Exclusion predicate -- R2 Step 5.3 IsVerified-STRICT (rewritten from the
#     pre-flip gp-only anchor, which necessarily went RED when the flip landed).
# ===========================================================================

class TestExclusionPredicateIsVerifiedStrict:
    """R2 Step 5.3 exclusion predicate (IsVerified-STRICT): a crop is
    forecastable iff its agronomy profile is ``IsVerified == 1`` AND has a
    usable GrowthPeriodDays. An unverified profile — even one holding a legacy
    GrowthPeriodDays — is EXCLUDED exactly like a NULL-gp crop; a NULL-gp
    profile is excluded regardless of the verified flag.

    *** REWRITTEN AT R2 STEP 5.3 from the pre-flip anchor
    (``TestExclusionPredicateAnchorPreStep5_3``). ***
    The old anchor asserted the pre-flip gp-only predicate (unverified-but-gp
    crop was INCLUDED / warned). The flip nulls an unverified profile's gp in
    the shared gate (load.resolve_forecast_gp, applied by
    features.build_crop_features), so that anchor necessarily went RED: an
    unverified meta now yields gp=None -> no label. This class re-expresses the
    NEW contract rather than re-greening the old assertion.
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
        """THE FLIP: IsVerified=0 + a legacy GrowthPeriodDays is NO LONGER
        forecastable. The unverified profile's gp is NOT honored -> no harvest
        horizon, no label -> the crop is dropped from the trainable/servable
        set, exactly as a NULL-gp crop is. (Under the pre-flip gp-only predicate
        this same input WAS forecastable and merely warned -- that is the
        behavior this rewrite deliberately inverts.)"""
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
        """REPINNED 2026-07-06 to the post-Step-5-Phase-1 live state: the DOA
        migration (RecodeAgronomyProfilesDoaVerifiedBatch1) applied 13
        owner-verified profiles carrying real Yala planting months (was 11
        gp-known / all 'Year-round' pre-Step-5)."""
        df = _db_or_skip()
        gp_known = df["GrowthPeriodDays"].notna()
        assert int(gp_known.sum()) == 13, \
            f"expected 13 gp-non-null crops (Step 5 Phase 1), got {int(gp_known.sum())}"
        # Exactly the 13 gp-known crops carry real Yala months now; the rest None.
        assert (df.loc[gp_known, "PlantingSeason"] == "Yala").all()
        assert df.loc[~gp_known, "PlantingSeason"].isna().all()

    def test_live_verified_set_equals_forecastable_set(self):
        """REWRITTEN 2026-07-06 from test_live_all_profiles_still_unverified_pre_step5.
        Under R2 Step 5.3 (IsVerified-strict) the forecastable set IS the verified
        set: Step 5 Phase 1 verified exactly the 13 crops that carry a gp, so
        IsVerified and GrowthPeriodDays-known must coincide row-for-row. (The old
        test pinned the pre-Step-5 all-unverified state and was self-flagged to be
        replaced the moment verified values landed.)"""
        df = _db_or_skip()
        verified = df["IsVerified"] == True   # noqa: E712
        gp_known = df["GrowthPeriodDays"].notna()
        assert int(verified.sum()) == 13, \
            f"expected 13 verified profiles (Step 5 Phase 1), got {int(verified.sum())}"
        assert (verified == gp_known).all(), \
            "IsVerified-strict: the verified set must equal the gp-known (forecastable) set"

    def test_live_planting_season_enc_distribution(self):
        """REPINNED 2026-07-06: PlantingSeasonEnc (features.py) now gives {1: the
        13 verified Yala crops, -1: the other 83} — Step 5 populated real Yala
        planting months, so the gp-known crops encode to 1 (Yala), NOT 0
        (Year-round) as they did pre-Step-5."""
        df = _db_or_skip()
        enc = df["PlantingSeason"].map(lambda s: F._PLANTING_SEASON_ENC.get(s, -1))
        gp_known = df["GrowthPeriodDays"].notna()
        assert (enc[gp_known] == 1).all()
        assert (enc[~gp_known] == -1).all()
        assert set(enc.unique()) == {1, -1}, \
            "the 13 verified crops encode to 1 (Yala); the rest to -1"
