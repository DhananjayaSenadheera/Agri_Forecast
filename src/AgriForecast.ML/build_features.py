"""Feature engineering entry point: builds the CropFeatureDaily feature store.

Pipeline: load raw prices, crops and weather -> leakage-safe feature build -> validate ->
persist (an idempotent full rebuild).

Every run also writes one IngestionRuns row with Source='FEATURE_BUILD' to the admin
Ingestion runs log: Running before the build starts, then the same row finalised as
Succeeded (with row counts and coverage) or Failed (with a capped ErrorSummary).

That audit write is FAIL-OPEN at every step, including the metrics derivation that runs
after the features are already stored: a DB hiccup, or a bug computing the coverage
numbers, is logged to stderr and swallowed, so a build that really succeeded can never be
recorded as Failed or exit non-zero over bookkeeping. A genuine build exception still
re-raises after the failure row is recorded, so the daily run keeps failing loudly.
"""
from __future__ import annotations

import sys

import pandas as pd

from agriforecast_ml import load, features, store, feature_run_log
from agriforecast_ml.db import get_engine
from agriforecast_ml.envfile import load_env_file


def _start_audit_run():
    """Best-effort: open the DB engine and insert the Running row. Returns
    ``(engine, run_id)``, or ``(None, None)`` if anything goes wrong (no DB
    configured, DB down, table absent, ...) — never raises."""
    try:
        engine = get_engine()
        run_id, _batch_id, _started_utc = feature_run_log.start_run(engine)
        return engine, run_id
    except Exception as exc:  # noqa: BLE001 -- audit bookkeeping must never block the build
        print(f"feature_run_log: Running row not recorded ({type(exc).__name__}: {exc}); "
              f"continuing build unaudited.", file=sys.stderr)
        return None, None


def _finish_audit_run_succeeded(engine, run_id, **kwargs) -> None:
    if engine is None or run_id is None:
        return
    try:
        feature_run_log.mark_succeeded(engine, run_id, **kwargs)
    except Exception as exc:  # noqa: BLE001 -- audit bookkeeping must never block the build
        print(f"feature_run_log: Succeeded row not recorded ({type(exc).__name__}: {exc}).",
              file=sys.stderr)


def _finish_audit_run_failed(engine, run_id, exc: Exception) -> None:
    if engine is None or run_id is None:
        return
    try:
        feature_run_log.mark_failed(engine, run_id, exc)
    except Exception as audit_exc:  # noqa: BLE001 -- never mask the real build error
        print(f"feature_run_log: Failed row not recorded ({type(audit_exc).__name__}: {audit_exc}).",
              file=sys.stderr)


def _report_forecastable_profiles(crops) -> None:
    """Print how many crops are forecastable, for visibility in the build log.

    A crop is forecastable only if its agronomy profile is verified AND has a usable
    GrowthPeriodDays. Unverified profiles are a hard skip, so a build that produced zero
    trainable crops should be obvious in the log.
    """
    if not {"IsVerified", "GrowthPeriodDays"}.issubset(crops.columns):
        return
    verified = crops["IsVerified"] == True  # noqa: E712 (NA-safe)
    forecastable = verified & crops["GrowthPeriodDays"].notna()
    print(f"Forecastable crops (IsVerified=1 AND gp known): "
          f"{int(forecastable.sum())}/{len(crops)}")


def main() -> None:
    # Load AGRI_DB_* from the gitignored .env so a bare `python build_features.py`
    # works without sourcing it first. Real env vars take precedence.
    load_env_file()
    print("=== AgriForecast feature build ===")

    audit_engine, audit_run_id = _start_audit_run()

    try:
        prices = load.load_prices()
        crops = load.load_crops()
        _report_forecastable_profiles(crops)
        weather = load.load_weather()
        fx = load.load_fx()
        sentiment = load.load_news_sentiment()
        policy = load.load_policy_flags()
        festivals = load.load_festivals()
        macro = load.load_macro_series()
        price_obs = load.load_price_observations()
        market_slugs = load.feature_safe_market_slugs()
        print(f"Loaded: prices={len(prices)} rows ({prices['CropId'].nunique()} crops), "
              f"crops={len(crops)}, weather={len(weather)} months, fx={len(fx)} rows, "
              f"sentiment={len(sentiment)} daily rows, policy={len(policy)} flags, "
              f"festivals={len(festivals)} rows, macro={len(macro)} vintages "
              f"({macro['SeriesCode'].nunique() if len(macro) else 0} series), "
              f"price_obs={len(price_obs)} rows over {len(market_slugs)} feature-safe "
              f"markets {market_slugs}")

        feats = features.build_all(prices, crops, weather, fx,
                                  sentiment=sentiment, policy=policy,
                                  festivals=festivals, macro=macro,
                                  price_obs=price_obs, market_slugs=market_slugs)

        n = len(feats)
        labelled = int(feats["LabelAvailable"].sum())
        crops_with_labels = feats.loc[feats["LabelAvailable"] == 1, "CropId"].nunique()
        print(f"Built {n} feature rows across {feats['CropId'].nunique()} crops.")
        print(f"Rows with a harvest-price label: {labelled} "
              f"({crops_with_labels} crops have GrowthPeriodDays).")
        print(f"Columns ({len(feats.columns)}): {list(feats.columns)}")

        written = store.write_features(feats)
        print(f"Persisted {written} rows to CropFeatureDaily.")

        try:
            if n:
                obs_dates = pd.to_datetime(feats["ObservationDate"])
                covered_from = obs_dates.min().date()
                covered_to = obs_dates.max().date()
                distinct_crops = int(feats["CropId"].nunique())
            else:
                covered_from = covered_to = None
                distinct_crops = 0
        except Exception as exc:  # noqa: BLE001 -- audit bookkeeping must never block the build
            # The build itself already succeeded, so a bug in THIS metrics derivation must not turn
            # an honest success into a Failed row. Record Succeeded without the metrics we could not
            # derive, rather than skipping the row entirely.
            print(f"feature_run_log: coverage/crop-count metrics not derived "
                  f"({type(exc).__name__}: {exc}); recording Succeeded without them.",
                  file=sys.stderr)
            covered_from = covered_to = None
            distinct_crops = None
        _finish_audit_run_succeeded(
            audit_engine, audit_run_id,
            rows_inserted=written,
            distinct_crops=distinct_crops,
            covered_from=covered_from,
            covered_to=covered_to,
        )
    except Exception as exc:
        _finish_audit_run_failed(audit_engine, audit_run_id, exc)
        raise


if __name__ == "__main__":
    main()
