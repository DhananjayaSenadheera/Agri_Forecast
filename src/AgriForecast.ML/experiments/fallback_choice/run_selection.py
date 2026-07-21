"""Offline evaluation for chip task_9b1cd894 — per-crop fallback selection.

Read-only against the live store. Reproduces EXACTLY what a v17 train run would
select (same code path: model.history_gated_crops for the model-served set, then
fallback_select.select_fallback_choices for the fallback map) and prints the
per-crop selection table + the aggregate gate. Writes selection_table.csv +
results.json. NO production changes, NO promotion.
"""
from __future__ import annotations
import json
from pathlib import Path

import pandas as pd

from agriforecast_ml.train import dataset, model
from agriforecast_ml.train.fallback_select import select_fallback_choices

OUT = Path(__file__).resolve().parent


def main():
    df = dataset.load_training_frame()
    served = model.history_gated_crops(df, model._DEFAULT_MIN_HISTORY_OBS)
    served_lower = sorted(str(c).lower() for c in served)
    print(f"Training rows: {len(df)}  crops: {df['CropId'].nunique()}")
    print(f"Model-served (history gate >= {model._DEFAULT_MIN_HISTORY_OBS}): "
          f"{len(served_lower)}  fallback crops: {df['CropId'].nunique() - len(served_lower)}")

    choice_map, table, aggregate = select_fallback_choices(df, served)

    tbl = pd.DataFrame(table)
    # order columns for the report
    cols = ["cropName", "cropCode", "cropId", "n_origins", "recency_mean_MAE",
            "serving_category_MAE", "serving_cropmedian_MAE",
            "carry_forward_MAE", "carry_forward_n", "seasonal_naive_MAE",
            "seasonal_naive_n", "best_challenger", "report_best_challenger",
            "report_best_MAE", "delta_pct", "switched"]
    cols = [c for c in cols if c in tbl.columns]
    tbl = tbl[cols].sort_values(["switched", "delta_pct"], ascending=[False, False])
    tbl.to_csv(OUT / "selection_table.csv", index=False)

    def f(x):
        return "-" if x is None or (isinstance(x, float) and pd.isna(x)) else (
            f"{x:.2f}" if isinstance(x, float) else str(x))

    print("\n=== Per-crop fallback selection (fallback segment) ===")
    print(f"{'crop':22} {'code':10} {'n':>5} {'recmean':>9} {'servCat':>9} "
          f"{'carry':>9} {'seas':>9} {'best':>13} {'delta%':>8} switch")
    for _, r in tbl.iterrows():
        dp = r.get("delta_pct")
        dp_s = "-" if dp is None or pd.isna(dp) else f"{dp*100:+.1f}"
        print(f"{f(r.get('cropName'))[:22]:22} {f(r.get('cropCode'))[:10]:10} "
              f"{int(r['n_origins']):>5} {f(r.get('recency_mean_MAE')):>9} "
              f"{f(r.get('serving_category_MAE')):>9} "
              f"{f(r.get('carry_forward_MAE')):>9} {f(r.get('seasonal_naive_MAE')):>9} "
              f"{f(r.get('best_challenger')):>13} {dp_s:>8} "
              f"{'YES' if r['switched'] else ''}")

    print("\n=== Near-misses (below 30 origins OR <10% improvement) ===")
    for _, r in tbl.iterrows():
        if r["switched"]:
            continue
        dp = r.get("delta_pct")
        if dp is not None and not pd.isna(dp) and dp > 0:
            why = []
            if int(r["n_origins"]) < 30:
                why.append(f"only {int(r['n_origins'])} origins")
            if dp < 0.10:
                why.append(f"only {dp*100:.1f}% better")
            if why:
                print(f"  {f(r.get('cropName'))[:22]:22} best={f(r.get('best_challenger'))} "
                      f"({', '.join(why)})")

    print("\n=== Aggregate gate ===")
    print(json.dumps(aggregate, indent=2, default=str))
    print(f"\nSWITCHED CROPS ({len(choice_map)}): {choice_map}")

    (OUT / "results.json").write_text(json.dumps({
        "min_history_obs": model._DEFAULT_MIN_HISTORY_OBS,
        "n_served": len(served_lower), "served_on_crops": served_lower,
        "choice_map": choice_map, "aggregate": aggregate,
        "table": table,
    }, indent=2, default=str))
    print("\nWrote selection_table.csv + results.json")


if __name__ == "__main__":
    main()
