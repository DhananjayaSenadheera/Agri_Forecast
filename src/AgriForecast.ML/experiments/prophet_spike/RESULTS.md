# Prophet gated R&D spike -- RESULTS (Steps 2-3)

EVALUATION-ONLY. No production/serving/registry change. ClickUp 86cahefgb.

Protocol: PLAN.md. Prophet = default Prophet + holidays-only, MAP (mcmc=0), seed 42, L-BFGS iter cap 10000, per-origin refit on `ds < origin`. v13 = per-origin point-in-time refit of the pooled history-gated hybrid (p50), predicted for the (crop, origin) row. Shared `y_true` = backward as-of of the daily series at `origin+h` (= v13's own label convention). Seasonal-naive = `y(origin+h - 365d)` (backward as-of, tol 30d). Carry-forward = `y(origin)`. Directional-accuracy reference = `y(origin)`.


## Capsicum (VEG000015) -- h=gp=75d

Matched origins: 169/169 (dropped 0; reasons: none).


### Overall (h=gp)

| model | MAE | RMSE | MAPE | dir-acc | n |
|---|---|---|---|---|---|
| prophet | 224.29 | 278.50 | 70.8% | 65.7% | 169 |
| snaive | 222.37 | 275.52 | 62.6% | 72.2% | 169 |
| carry | 264.07 | 339.67 | 75.1% | 0.6% | 169 |
| v13 | 184.89 | 243.06 | 43.6% | 74.6% | 169 |

### Per fold (h=gp), MAE [dir-acc]

| fold | prophet | snaive | carry | v13 | n |
|---|---|---|---|---|---|
| 1 | 236.0 [47%] | 153.2 [65%] | 223.7 [2%] | 160.9 [61%] | 62 |
| 2 | 151.8 [83%] | 248.8 [77%] | 235.2 [0%] | 236.1 [75%] | 53 |
| 3 | 282.0 [70%] | 275.9 [76%] | 338.8 [0%] | 162.2 [89%] | 54 |

**Gate (Capsicum):** Prophet 224.29 vs seasonal-naive 222.37 (LOSES) & vs v13 184.89 (LOSES) -> FAIL


### Context h=7d (no v13) -- matched 169/169

| model | MAE | RMSE | MAPE | dir-acc | n |
|---|---|---|---|---|---|
| prophet | 189.30 | 233.20 | 53.0% | 46.8% | 169 |
| snaive | 211.30 | 264.57 | 58.5% | 52.7% | 169 |
| carry | 77.23 | 106.70 | 17.5% | 7.1% | 169 |

### Context h=30d (no v13) -- matched 169/169

| model | MAE | RMSE | MAPE | dir-acc | n |
|---|---|---|---|---|---|
| prophet | 242.21 | 289.70 | 70.2% | 59.2% | 169 |
| snaive | 219.99 | 272.27 | 60.3% | 62.1% | 169 |
| carry | 190.44 | 251.11 | 44.7% | 5.3% | 169 |

### Splice sensitivity (h=gp) -- pre=110 post=59 (splice 2025-05-05)

Pre-splice-target origins only:

| model | MAE | RMSE | MAPE | dir-acc | n |
|---|---|---|---|---|---|
| prophet | 195.60 | 235.36 | 55.7% | 65.5% | 110 |
| snaive | 183.09 | 224.97 | 49.1% | 72.7% | 110 |
| carry | 234.55 | 286.97 | 59.6% | 0.9% | 110 |
| v13 | 186.66 | 233.16 | 44.2% | 70.0% | 110 |

Post-splice-target origins only:

| model | MAE | RMSE | MAPE | dir-acc | n |
|---|---|---|---|---|---|
| prophet | 277.77 | 344.80 | 99.0% | 66.1% | 59 |
| snaive | 295.59 | 350.82 | 87.8% | 71.2% | 59 |
| carry | 319.11 | 420.64 | 104.0% | 0.0% | 59 |
| v13 | 181.58 | 260.52 | 42.6% | 83.0% | 59 |

## Ridge Gourd (VEG000057) -- h=gp=65d

Matched origins: 171/171 (dropped 0; reasons: none).


### Overall (h=gp)

| model | MAE | RMSE | MAPE | dir-acc | n |
|---|---|---|---|---|---|
| prophet | 88.59 | 106.61 | 56.9% | 70.2% | 171 |
| snaive | 85.56 | 104.44 | 50.6% | 70.8% | 171 |
| carry | 106.74 | 136.51 | 65.3% | 2.9% | 171 |
| v13 | 84.07 | 106.46 | 47.1% | 71.4% | 171 |

### Per fold (h=gp), MAE [dir-acc]

| fold | prophet | snaive | carry | v13 | n |
|---|---|---|---|---|---|
| 1 | 92.8 [66%] | 58.5 [77%] | 99.3 [2%] | 75.5 [76%] | 62 |
| 2 | 54.1 [70%] | 99.0 [69%] | 74.2 [6%] | 72.7 [67%] | 54 |
| 3 | 117.7 [75%] | 102.9 [65%] | 147.1 [2%] | 104.9 [71%] | 55 |

**Gate (Ridge Gourd):** Prophet 88.59 vs seasonal-naive 85.56 (LOSES) & vs v13 84.07 (LOSES) -> FAIL


### Context h=7d (no v13) -- matched 171/171

| model | MAE | RMSE | MAPE | dir-acc | n |
|---|---|---|---|---|---|
| prophet | 69.04 | 81.50 | 45.3% | 53.2% | 171 |
| snaive | 80.29 | 100.76 | 48.5% | 54.4% | 171 |
| carry | 37.40 | 49.00 | 21.1% | 5.9% | 171 |

### Context h=30d (no v13) -- matched 171/171

| model | MAE | RMSE | MAPE | dir-acc | n |
|---|---|---|---|---|---|
| prophet | 79.97 | 95.38 | 52.9% | 66.1% | 171 |
| snaive | 82.27 | 104.37 | 49.2% | 71.4% | 171 |
| carry | 77.98 | 97.58 | 45.3% | 3.5% | 171 |

### Splice sensitivity (h=gp) -- pre=111 post=60 (splice 2025-05-05)

Pre-splice-target origins only:

| model | MAE | RMSE | MAPE | dir-acc | n |
|---|---|---|---|---|---|
| prophet | 75.39 | 91.28 | 48.1% | 66.7% | 111 |
| snaive | 77.00 | 94.62 | 41.4% | 73.0% | 111 |
| carry | 85.63 | 108.74 | 52.3% | 3.6% | 111 |
| v13 | 72.72 | 90.32 | 40.3% | 71.2% | 111 |

Post-splice-target origins only:

| model | MAE | RMSE | MAPE | dir-acc | n |
|---|---|---|---|---|---|
| prophet | 113.00 | 130.30 | 73.1% | 76.7% | 60 |
| snaive | 101.38 | 120.50 | 67.6% | 66.7% | 60 |
| carry | 145.79 | 176.73 | 89.4% | 1.7% | 60 |
| v13 | 105.06 | 131.18 | 59.7% | 71.7% | 60 |

## VERDICT (ship gate)

Gate: Prophet ships only if it beats BOTH seasonal-naive AND v13 at h=gp on BOTH crops.

| crop | Prophet MAE | seasonal-naive MAE | v13 MAE | beats sn? | beats v13? | verdict |
|---|---|---|---|---|---|---|
| Capsicum | 224.29 | 222.37 | 184.89 | N | N | FAIL |
| Ridge Gourd | 88.59 | 85.56 | 84.07 | N | N | FAIL |

**SHIP DECISION: DO NOT SHIP (Prophet fails the gate).** Nothing is promoted or committed regardless -- this is an R&D spike.


## Leakage self-check

- v13: per-origin train cut `ObservationDate < origin` AND `HarvestDate < origin` (purge). Export asserted `max_train_obs < origin` for every origin; `v13_leak_bad` = {'VEG000015': 0, 'VEG000057': 0}.

- Prophet: per-origin `train = series[ds < origin]`, in-code assert `train.ds.max() < origin`; `leak_bad` = {'VEG000015': 0, 'VEG000057': 0}.

- Prophet seasonalities activated (defaults): {'VEG000015': {'weekly|yearly': 169}, 'VEG000057': {'weekly|yearly': 171}}.

- Wall time: Stage A export 422.7s, Stage B Prophet 93.9s.


## Caveats

1. **v13 information asymmetry:** v13 uses lags/rollings/festival/macro/spread + cross-crop pooling; Prophet uses univariate price + holidays only. Same target/origin, different feature sets -- a 'which predictor wins,' not 'same features.'

2. **Fold geometry:** origins = the crop's v13 feature ObservationDates in the last 40%, split into 3 sequential blocks, weekly cadence. v13's own TRAINING rows differ (pooled, purged) even at matched origins.

3. **Series splice** (HARTI->Dambulla-DEC, 2025-05-05): both models cross it; v13 trained through it, Prophet sees it cold. See per-crop splice tables.

4. v13 published pooled CV MAE (100.31) is NOT the comparator; only the per-crop, same-origin v13 number above is a fair opponent.

