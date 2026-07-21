# Per-crop fallback-predictor selection — RESULTS (chip task_9b1cd894)

Read-only against the live store (v16 frame: 62,896 labelled rows, 81 crops,
21 model-served / 60 fallback). Seed 42. Reproduce with:

```
PYTHONPATH=. .venv/bin/python experiments/fallback_choice/run_selection.py
```

Machinery lives in `agriforecast_ml/train/fallback_select.py` (the trainer calls
`select_fallback_choices` and ships the winning map in the signed payload under
`fallback["choice"]`; serving reads it in `serving/predict.py`). This experiment
runs the SAME code path, so it is exactly what a v17 train run would select.
**No production changes, no retrain, no promotion here** — that is a separate
hub-run step.

> ⚠️ **Point-in-time snapshot.** `selection_table.csv` and every number below are
> a snapshot of the PRE-migration store (before `20260721135936_FixGarlicAliasMapping`).
> The selection map is recomputed from the live frame on EVERY train run, so the
> v17 map will reflect the store as it is at that run — treat this table as
> indicative, not the final v17 selection.

## VERDICT: SWITCH 22 fallback crops to carry-forward. Big, fail-safe win.

The recency-mean incumbent mis-serves recent-onset / volatile crops badly. All
60 fallback crops have <365 labelled rows, ALL starting 2025-05-05, so in the
walk-forward folds they have little/no train history → recency-mean collapses to
the GLOBAL mean (e.g. Mukunuwenna: true harvest ~Rs 27, recency-mean predicts
~204). Carry-forward (last observed AvgPrice) tracks the current level and wins
by 34–98 % on the 22 switched crops.

### Gate configuration (hub-set)
- switch away from recency-mean only if a **servable** challenger beats it by
  **≥10 % MAE** on **≥30 matched origins** AND does **not regress vs the REAL
  serving incumbent** (the category-median tier a <365-row crop actually gets
  today);
- **only carry-forward is shipped** (trivially servable = the AvgPrice already on
  the fetched feature row). Seasonal-naive is evaluated + reported but not wired
  to serving (needs a serve-time 1-year-lookback query — a separate change);
- aggregate: pooled (origin-weighted) fallback MAE must not regress and no
  switched crop may be worse than its own recency-mean baseline.

### Aggregate gate (fallback segment, 14,461 walk-forward rows)
| pooled MAE | value |
|---|---:|
| recency-mean incumbent (strawman — inflated by cold-start global mean) | 164.95 |
| … with the 22 switches applied | **104.70** |
| **REAL serving incumbent (category-median tier)** | **192.91** |
| … with the 22 switches applied | **122.24** |
| no switched crop regresses | ✅ |

Honest read: vs what serving deploys today (category tier), the 22 switches cut
pooled fallback MAE **192.91 → 122.24 (−37 %)**. The recency-mean row is reported
because the gate spec names it, but it is a strawman here (cold-start global-mean
fallback), so the category-tier comparison is the load-bearing one.

### Switched crops (22, all → carry-forward)
Ordered by improvement vs recency-mean. `servCat` = real serving incumbent MAE.

| crop | code | n | recmean | servCat | carry | Δ% vs recmean |
|---|---|--:|--:|--:|--:|--:|
| Mukunuwenna | VEG000040 | 364 | 203.88 | 86.40 | 4.68 | +97.7 |
| Gotukola | VEG000029 | 301 | 204.17 | 86.69 | 4.91 | +97.6 |
| Water Spinach | VEG000067 | 363 | 204.22 | 86.74 | 5.23 | +97.4 |
| Kohila | VEG000034 | 173 | 128.41 | 16.21 | 15.91 | +87.6 |
| Chaw-Chaw | VEG000018 | 264 | 159.46 | 42.57 | 20.69 | +87.0 |
| Gram | VEG000030 | 246 | 120.06 | 237.53 | 24.14 | +79.9 |
| Green Gram | VEG000032 | 327 | 347.21 | 464.69 | 90.29 | +74.0 |
| Pumpkin-Malashian | VEG000052 | 306 | 161.78 | 59.76 | 45.65 | +71.8 |
| Pumpkin-Big | VEG000051 | 50 | 139.20 | 44.10 | 39.35 | +71.7 |
| Cooking Melon | VEG000019 | 335 | 157.40 | 60.06 | 45.17 | +71.3 |
| Dry Chillies | VEG000025 | 232 | 635.32 | 752.79 | 186.25 | +70.7 |
| Peanuts | VEG000045 | 298 | 261.71 | 379.02 | 84.06 | +67.9 |
| Soya Bean | VEG000060 | 219 | 131.42 | 248.72 | 46.64 | +64.5 |
| Big Onion | VEG000007 | 310 | 142.81 | 260.31 | 50.94 | +64.3 † |
| Thumbakarawila | VEG000064 | 309 | 354.14 | 456.62 | 128.69 | +63.7 |
| Cowpea | VEG000021 | 291 | 334.83 | 452.30 | 125.70 | +62.5 |
| Finger Millet | VEG000027 | 125 | 166.64 | 284.08 | 69.52 | +58.3 |
| Red Banana | FRT000022 | 299 | 87.85 | 209.87 | 46.54 | +47.0 |
| Lotus Roots | VEG000038 | 109 | 286.35 | 403.85 | 160.09 | +44.1 |
| Big Onion Lanka | VEG000009 | 81 | 162.34 | 138.02 | 97.56 | +39.9 |
| Sesame | VEG000058 | 311 | 183.57 | 298.92 | 117.40 | +36.0 |
| Thithbatu | VEG000063 | 298 | 253.83 | 371.31 | 167.03 | +34.2 |

† **Big Onion VEG000007 will NOT reappear at the v17 recomputation.** This row
predates `20260721135936_FixGarlicAliasMapping`, which moved ALL 392 of Big
Onion's price rows to the new crop Garlic (VEG000071). Big Onion now has zero
price rows, so it will not be a switch candidate on the live frame. This is the
expected behavior of a recomputed-each-train selection map, not an inconsistency.

### Withheld on purpose (near-misses)
- **Turmeric** (VEG000066): carry only +0.3 % over recency-mean — below the 10 %
  bar. No switch.
- **Carrot-Jaffna** (VEG000016): only 24 origins (<30). No switch.
- **Corns / Jackfruit / Watermelon / Mango-Malu / Cabbage** etc.: carry-forward
  beats recency-mean but does **not** beat the category-median tier serving
  deploys today, so the no-regression guard correctly withholds them.

### Seasonal-naive future upside (evaluated, NOT shipped)
On 12 crops a seasonal-naive fallback (price ~1 year before harvest) would beat
BOTH recency-mean (≥10 %) AND the category incumbent (≥10 %) on ≥30 origins, e.g.
Jackfruit (14.5 vs servCat 33.1), Watermelon (20.7 vs 37.5), Avocado (82.6 vs
355.6), Passion (126.0 vs 323.0). Wiring a serve-time 1-year-lookback query +
leakage guard is a follow-up chip; the machinery already evaluates it
(`report_best_challenger` in `selection_table.csv`).

## Leakage / point-in-time discipline
- Fold structure = the SAME purged expanding-window walk-forward as the hybrid
  gate (`model.purged_walk_forward`); a train row is purged if its harvest date
  falls on/after the test-window start.
- Candidates for a fold's test rows use TRAIN only (recency-mean) or
  information known at the observation date (carry-forward = AvgPrice at obs;
  seasonal-naive = a price at/before HarvestDate−365, **NaN for gp≥365** so it
  can never peek forward).
- The per-crop choice is a static function of the training data → at serve time
  it is fully point-in-time (serving just reads the shipped map).

## Serving change (v17 payload only; current v16 fails closed)
For a crop in `fallback["choice"]` with `carry_forward`, serving re-centres the
fallback interval on the last NON-NULL AvgPrice, keeping the crop's OWN per-crop
quantile half-spreads (absolute Rs width, clamped ≥0). Confidence stays
**"Low"**; `reasonCode` (`not_model_served`), `confidenceReason`,
`activePredictor` (`crop_mean_fallback`) and `fallbackTier` are ALL unchanged —
only the numbers improve. Any crop absent from the map, any old payload, any
missing/invalid/**stale** price → recency-mean/category incumbent (today's
behavior). Both `predict_harvest` and `timeline` route through the SAME
`_carry_forward_price` (last non-null + the cap), so they never disagree even
when the newest feature row has a NULL AvgPrice.

**Staleness cap (60 days).** The carry-forward anchor is only trusted if its
observation is within `_CARRY_FORWARD_STALENESS_DAYS = 60` of the request's
as-of/plant date — mirroring the P3 macro convention (`_MACRO_STALENESS_DAYS =
60` in features.py). A switched crop that goes silent between retrains would
otherwise anchor on an arbitrarily old price; past the cap serving FAILS CLOSED
to the incumbent (category/crop-median) tier rather than serve a stale level. 60d
matches the daily-ingestion cadence (a crop reporting even monthly stays fresh)
while catching genuinely dormant crops.

## Out of scope (noted, not touched)
- Model-served crops (the Ambul 24.38-vs-26.19 model-vs-fallback observation) —
  a separate refinement, not folded in here.
