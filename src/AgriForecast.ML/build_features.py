"""Feature engineering entry point — builds the CropFeatureDaily feature store.

Pipeline: load raw (prices/crops/weather) -> leakage-safe feature build ->
validate -> persist (idempotent full rebuild).
"""
from __future__ import annotations

from agriforecast_ml import load, features, store
from agriforecast_ml.envfile import load_env_file


def _report_forecastable_profiles(crops) -> None:
    """R2 Step 5.3: the exclusion predicate is now IsVerified-STRICT — a crop is
    forecastable ONLY IF its profile is IsVerified=1 AND has a usable
    GrowthPeriodDays (applied in features.build_crop_features via
    load.resolve_forecast_gp). This is no longer a WARN-through: unverified
    profiles are a hard skip. Print the forecastable-crop count for visibility so
    a build that produced ZERO trainable crops (e.g. before the DOA-verified
    values migration is applied) is obvious in the log.
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
    prices = load.load_prices()
    crops = load.load_crops()
    _report_forecastable_profiles(crops)
    weather = load.load_weather()
    fx = load.load_fx()
    sentiment = load.load_news_sentiment()
    policy = load.load_policy_flags()
    festivals = load.load_festivals()
    macro = load.load_macro_series()
    print(f"Loaded: prices={len(prices)} rows ({prices['CropId'].nunique()} crops), "
          f"crops={len(crops)}, weather={len(weather)} months, fx={len(fx)} rows, "
          f"sentiment={len(sentiment)} daily rows, policy={len(policy)} flags, "
          f"festivals={len(festivals)} rows, macro={len(macro)} vintages "
          f"({macro['SeriesCode'].nunique() if len(macro) else 0} series)")

    feats = features.build_all(prices, crops, weather, fx,
                              sentiment=sentiment, policy=policy,
                              festivals=festivals, macro=macro)

    n = len(feats)
    labelled = int(feats["LabelAvailable"].sum())
    crops_with_labels = feats.loc[feats["LabelAvailable"] == 1, "CropId"].nunique()
    print(f"Built {n} feature rows across {feats['CropId'].nunique()} crops.")
    print(f"Rows with a harvest-price label: {labelled} "
          f"({crops_with_labels} crops have GrowthPeriodDays).")
    print(f"Columns ({len(feats.columns)}): {list(feats.columns)}")

    written = store.write_features(feats)
    print(f"Persisted {written} rows to CropFeatureDaily.")


if __name__ == "__main__":
    main()
