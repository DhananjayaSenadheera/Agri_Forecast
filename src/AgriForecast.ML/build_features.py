"""Feature engineering entry point — builds the CropFeatureDaily feature store.

Pipeline: load raw (prices/crops/weather) -> leakage-safe feature build ->
validate -> persist (idempotent full rebuild).
"""
from __future__ import annotations

from agriforecast_ml import load, features, store


def main() -> None:
    print("=== AgriForecast feature build ===")
    prices = load.load_prices()
    crops = load.load_crops()
    weather = load.load_weather()
    fx = load.load_fx()
    sentiment = load.load_news_sentiment()
    policy = load.load_policy_flags()
    festivals = load.load_festivals()
    print(f"Loaded: prices={len(prices)} rows ({prices['CropId'].nunique()} crops), "
          f"crops={len(crops)}, weather={len(weather)} months, fx={len(fx)} rows, "
          f"sentiment={len(sentiment)} daily rows, policy={len(policy)} flags, "
          f"festivals={len(festivals)} rows")

    feats = features.build_all(prices, crops, weather, fx,
                              sentiment=sentiment, policy=policy,
                              festivals=festivals)

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
