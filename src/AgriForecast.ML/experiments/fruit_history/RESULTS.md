# Fruit-history GATED experiment — RESULTS

Seed 42. v13-faithful purged walk-forward, fold blocks FIXED from arm A and reused
for B/C (matched origins). All in-memory; production `CropFeatureDaily` untouched.
Raw numbers in `results.json`, `matched.json`, `per_row_{A,B,C}.csv`.

## VERDICT: PARTIAL SHIP (Ambul + Seeni), REJECT (Kolikuttu). Papaya N/A.

| Crop | gp | matched DEC-era rows | A (re-scored incumbent) | B (extended-history model) | best naive baseline | B beats A? | B beats baseline? | ship? |
|------|----|--------------------:|------------------------:|---------------------------:|--------------------:|:----------:|:-----------------:|:-----:|
| **Ambul** | 90 | 315 | **144.60** (recency-mean fallback) | **26.08** | 30.04 (carry) | YES | YES | **SHIP** |
| **Seeni** | 135 | 272 | **152.16** (recency-mean fallback) | **19.18** | 22.37 (carry) | YES | YES | **SHIP** |
| **Kolikuttu** | 120 | 283 | **35.67** (recency-mean fallback) | **47.34** | 35.67 (recmean) | NO | NO | **REJECT** |
| Papaya | NULL | — | — | — | — | — | — | excluded (perennial, no harvest label) |

All comparisons are on the **DEC-era test rows present in BOTH arms** (obs ≥
2025-05-05). In arm A these fruits are thin (<365 labelled rows) → served by the
recency-mean fallback; in arm B they cross the 365 gate → history-gated pooled
model. So the comparison is honestly "poor recency-mean fallback vs pooled model
with real history", on identical rows.

Pooled matched fruit (all 3, DEC-era): A 111.53 → **B 30.84**.

### Mechanism
- Ambul/Seeni: extended history promotes them from a *badly-mis-serving*
  recency-mean fallback (144/152 MAE — worse than plain carry-forward!) to a
  model that captures level + seasonality. Robust across all 3 folds.
- Kolikuttu: shorter Pettah history (from 2020, 1.3k rows vs 2.4k) + a **rising
  DEC-era regime** makes the model UNDER-predict (bias −23.6). Its recency-mean
  fallback (35.67) is already decent, so the model can't beat it. Same
  stale-history-in-a-rising-regime failure mode as the v13.1 thin-crop post-mortem.
- Splice level gap (Pettah > Dambulla) is DOMINATED by the temporal regime: all
  three B models under-predict (bias −7.7 / −15.3 / −23.6), i.e. stale lower
  history drags predictions DOWN, not up.

## Arm C (splice-dummy `SourceIsHarti`) — matched DEC-era hybrid MAE
| Crop | A | B | C |
|------|--:|--:|--:|
| Ambul | 144.60 | 26.08 | **24.11** |
| Seeni | 152.16 | 19.18 | **16.75** |
| Kolikuttu | 35.67 | 47.34 | 42.85 |

C marginally helps all three (a clean flag the tree can split on) but does NOT
rescue Kolikuttu (42.85 still loses to 35.67). No fancy re-scaling attempted, per
plan. C is a minor, optional refinement — not decision-changing.

## Guard — does fruit history hurt the other 79 crops?
Non-fruit test rows are byte-identical between arms (33,671 rows); only the pooled
model is retrained with fruit rows.
| Arm | non-fruit pooled MAE | Δ vs A |
|-----|--------------------:|------:|
| A | 116.00 | — |
| B | 116.74 | **+0.74 (+0.6 %)** |
| C | 117.18 | +1.18 |

Small but real, deterministic degradation from adding the fruits to the pooled
gated training set. The overall pooled MAE *improves* (115.89 → 112.57) only
because B has 3× more well-predicted fruit test rows — a row-mix artifact, not a
like-for-like gain. Honest read: extended fruit history slightly perturbs the
other crops.

## Caveat quantification (arm B fruit test rows)
- 2,508 fruit test rows / 861 origins; only **205 rows / 80 origins straddle the
  2025-05-05 splice**. So splice-crossing folds are a small minority — most of the
  A/B matched improvement comes from the model simply having a price history to
  learn from, not from the splice window itself.
- B adds **5,108 pre-splice fruit TRAIN rows** (the mechanism under test).

## Cold-start FRT category fallback (Kolikuttu's UI uses this TODAY)
Reproduces `_crop_fallback` `by_category['FRT']` quantiles over labelled rows of
FRT-category crops.
| | p10 | p50 | p90 | n_rows |
|--|----:|----:|----:|------:|
| A | 75.0 | 200.0 | 405.0 | 4,734 |
| B | 55.0 | 125.0 | 364.5 | 9,842 |

⚠️ The extended prior shifts DOWN hard (p50 200 → 125), because 6k+ rows of
2015–2024 Pettah history (lower nominal, inflation-unadjusted levels) now dominate
the pool. Present-day Kolikuttu sits ~300–420. So the raw-splice category prior is
**LESS representative of current levels, likely a WORSE fallback**, not better —
the hoped-for "better-informed fallback" does NOT materialise without recency
weighting. Do not ship the extended-history category prior as-is.

## Directional accuracy (reporting)
Fruit hybrid: A 0.654 vs B 0.650 (≈flat). Pooled: A 0.677 vs B 0.675.

## Recommendation
1. **Adopt extended HARTI-Pettah history for Ambul (FRT000003) + Seeni (FRT000006)**
   — large, fold-robust wins beating both re-scored-A and the best naive baseline.
2. **Do NOT adopt for Kolikuttu (FRT000005)** — loses to its recency-mean fallback;
   revisit only with recency-weighting / a rising-regime correction.
3. Papaya (FRT000018): moot — perennial, no harvest label.
4. Flag the ~0.6 % non-fruit pooled degradation to the owner as the cost of adding
   fruits to the pooled gated set.
5. Do NOT ship the extended-history FRT cold-start prior as-is (stale-level
   contamination). If a fallback refresh is wanted, use recency-weighted quantiles.
6. Optional arm C splice-dummy: minor gain for Ambul/Seeni, not decision-changing.

No production changes made. Adoption (a real store rebuild + gated retrain at
Step 7 discipline, re-scoring the incumbent on the new frame) is a separate,
owner-approved step.
