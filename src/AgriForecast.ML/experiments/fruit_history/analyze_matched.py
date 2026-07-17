"""Matched-origin analysis: compare A vs B ONLY on the fruit test rows present in
BOTH arms (the DEC-era, obs >= 2025-05-05 rows). This is the honest per-crop
comparison the ship gate needs — same rows, different training history (A serves
recency-mean fallback, B serves the history-gated pooled model)."""
from __future__ import annotations
import json
from pathlib import Path
import numpy as np
import pandas as pd

OUT = Path(__file__).resolve().parent
FRUITS = {
    "bc018387-a122-47b7-87be-1621bea29b51": ("Ambul", 90),
    "02dc3010-7139-4212-8d6d-32685db85657": ("Kolikuttu", 120),
    "83f194dd-4ce9-456a-9aac-bfd98e287a7f": ("Seeni", 135),
}
SPLICE = pd.Timestamp("2025-05-05")


def mae(y, p):
    y, p = np.asarray(y, float), np.asarray(p, float)
    m = ~(np.isnan(y) | np.isnan(p))
    return round(float(np.mean(np.abs(y[m] - p[m]))), 2) if m.any() else None


def bias(y, p):
    y, p = np.asarray(y, float), np.asarray(p, float)
    m = ~(np.isnan(y) | np.isnan(p))
    return round(float(np.mean(p[m] - y[m])), 2) if m.any() else None


prA = pd.read_csv(OUT / "per_row_A.csv", parse_dates=["obs", "harvest"])
prB = pd.read_csv(OUT / "per_row_B.csv", parse_dates=["obs", "harvest"])

matched = {}
for cid, (label, gp) in FRUITS.items():
    a = prA[prA["CropId"] == cid][["obs", "y", "hybrid", "recmean", "carry", "seasnaive"]]
    b = prB[prB["CropId"] == cid][["obs", "y", "hybrid", "recmean", "carry", "seasnaive"]]
    j = a.merge(b, on="obs", suffixes=("_A", "_B"))
    # sanity: same label on matched rows
    assert np.allclose(j["y_A"], j["y_B"]), f"{label}: label mismatch on matched rows"
    matched[label] = {
        "gp": gp,
        "matched_rows": int(len(j)),
        "obs_min": str(j["obs"].min().date()) if len(j) else None,
        "obs_max": str(j["obs"].max().date()) if len(j) else None,
        "A_hybrid_MAE": mae(j["y_A"], j["hybrid_A"]),   # recency-mean fallback
        "B_hybrid_MAE": mae(j["y_A"], j["hybrid_B"]),   # gated pooled model
        "B_hybrid_bias": bias(j["y_A"], j["hybrid_B"]),  # +ve => B predicts high (Pettah pull)
        "baseline_recmean_MAE": mae(j["y_A"], j["recmean_A"]),
        "baseline_carry_MAE": mae(j["y_A"], j["carry_A"]),
        "baseline_seasnaive_MAE": mae(j["y_A"], j["seasnaive_B"]),
        "best_baseline_MAE": min(x for x in [mae(j["y_A"], j["recmean_A"]),
                                             mae(j["y_A"], j["carry_A"]),
                                             mae(j["y_A"], j["seasnaive_B"])]
                                 if x is not None),
    }
    m = matched[label]
    m["B_beats_A"] = m["B_hybrid_MAE"] < m["A_hybrid_MAE"]
    m["B_beats_best_baseline"] = m["B_hybrid_MAE"] < m["best_baseline_MAE"]

print(json.dumps(matched, indent=2))
(OUT / "matched.json").write_text(json.dumps(matched, indent=2))

# pooled matched (all 3 fruits, DEC-era)
rows = []
for cid, (label, gp) in FRUITS.items():
    a = prA[prA["CropId"] == cid][["obs", "y", "hybrid"]]
    b = prB[prB["CropId"] == cid][["obs", "hybrid"]]
    j = a.merge(b, on="obs", suffixes=("_A", "_B"))
    rows.append(j)
allj = pd.concat(rows)
print("\nPOOLED MATCHED FRUIT (DEC-era rows present in both):")
print(f"  matched_rows={len(allj)}  A_MAE={mae(allj['y'], allj['hybrid_A'])}  "
      f"B_MAE={mae(allj['y'], allj['hybrid_B'])}")
