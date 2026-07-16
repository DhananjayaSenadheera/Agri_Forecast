# Prophet gated R&D spike — protocol plan (Step 1 output)

EVALUATION-ONLY. No production code, no serving change, no registry write. ClickUp
86cahefgb (D3 gate). This file is the walk-forward + comparison protocol the later
steps will implement; the owner approves each step. Nothing here is committed as a
production behavior.

## 0. What Prophet models (and what it does NOT)
- Prophet fits the **per-crop daily `AvgPrice` series** `y(t)` — a *parallel*
  univariate pipeline. It NEVER consumes `LabelHarvestPrice` (`AvgPrice.shift(-gp)`),
  never touches the pooled harvest-price frame, and is never a drop-in "third model".
- Regressors: **holidays only**, via `to_prophet_holidays(load_festivals())`
  (load.py:481). NO macro / weather / spread / lag regressors.
- Estimation: **MAP** (cmdstanpy `optimize`, L-BFGS) — `mcmc_samples = 0`. NO MCMC.

## 1. Candidate crops + series definition (owner picks; recommendation)
Two crops, both v13 **model-served** (in `served_on_crops`) so a genuine head-to-head
against v13's ML path is possible — not merely against the fallback:

| Crop | CropCode | GrowthPeriodDays (gp) = matched horizon | v13-served |
|------|----------|------------------------------------------|-----------|
| Capsicum    | VEG000015 | 75 d | yes |
| Ridge Gourd | VEG000057 | 65 d | yes |

**Primary series definition (recommended): per-crop daily `AvgPrice` from
`load_prices()` (MarketPrices), `AvgPrice = (MinPrice + MaxPrice)/2`, one blended
value per (crop, date).** Rationale: this is the EXACT series v13's harvest label is
derived from (`dataset` builds `LabelHarvestPrice` from this `AvgPrice`), so the
matched-horizon comparison targets the *same* underlying quantity — the only way the
head-to-head is honest on the target itself.
- Depth: Capsicum 2,814 daily points / Ridge Gourd 2,815 over 2015-06-22..2026-07-10
  (~11.1 seasonal cycles). ~30% of calendar days absent = normal market-closure
  sparsity (weekends/poya/festival closures), which Prophet handles natively (it fits
  on whatever `ds` rows exist; it does not require regular spacing).
- **CAVEAT / risk (flag, do not paper over):** this MarketPrices series is a *splice*
  — HARTI retail before 2025-05-05, Dambulla-DEC wholesale from 2025-05-05 (Ridge
  Gourd/Beans: HARTI through 2025-06-30). That source change is a likely LEVEL/REGIME
  shift near mid-2025. Holidays-only Prophet cannot model it (a source regressor /
  changepoint is out of D3 scope). Note it in the training report; if the splice
  dominates error, fall back to the alternative below.
- **Max single gap = 94 days** (systematic, same across crops — one collection hole).
  Prophet tolerates it but has no signal across it; report per-fold whether an origin's
  forecast window spans it.

**Alternative series definition:** Dambulla-only `AvgPrice` from `load_price_observations()`
(single market, single source, no splice; Capsicum Dambulla = 2,810 pts, Ridge Gourd
Dambulla = 2,800). Cleaner as a *series* but it is NOT the series v13 was trained/labelled
on, so a v13 comparison becomes Prophet-on-series-B vs v13-on-series-A (target mismatch).
Use only if the splice makes the primary series unusable — and if used, the v13
comparison must be dropped to "seasonal-naive only" and flagged as not apples-to-apples.

## 2. Walk-forward split (daily series)
Expanding-window, per crop, mirroring the v13 spirit (`purged_walk_forward`,
train/model.py:233) but on the daily series with a **horizon = gp** purge:
- Unique observation dates sorted; **test region = last 40%** of the series span.
- Split the test region into **3 sequential origin blocks** (folds), matching v13's
  `n_folds=3`.
- For fold *i*: **origin dates** = the block's dates. Prophet trains on all rows with
  `ds` **strictly < first origin of the block MINUS gp** is NOT required — instead the
  purge is on the LABEL: train on `ds < origin` for each origin, and to keep folds
  disjoint we fit **once per fold** on `ds < block_start`, then score every origin in
  the block by forecasting `origin + gp`. Because the forecast target `origin + gp`
  for a late-in-block origin could exceed `block_start`, we additionally require the
  **training cut = block_start** (no training row at or after the block start), which
  guarantees no target date `origin+gp` was seen in training only when `gp <=` block
  width; where `gp >` block width we shrink the block or reduce to per-origin refit —
  decided at build time and reported. (Simplest safe default: **per-origin refit** —
  fit on `ds < origin`, forecast `origin+gp`. More fits but zero target leakage. Use
  this unless wall-time forces the per-block variant.)
- **Horizon(s):** primary = **gp** (Capsicum 75 d, Ridge Gourd 65 d). Also report
  h ∈ {7, 30, gp} for context, but the SHIP GATE is judged at h = gp only (matched to
  v13).
- **Step size:** origins spaced every **7 days** within the test region (weekly
  origins) to get enough matched points without refitting on every calendar day.

## 3. Baselines
- **Seasonal-naive (primary daily baseline):** `yhat(origin + gp) = y(target_date − 365d)`
  = the actual price on the same calendar day one year before the target (nearest
  available prior observation if that exact day is absent). This is the honest annual-
  seasonality baseline for a daily price series.
- **Carry-forward (trivial):** `yhat(origin + gp) = y(origin)` — today's price held to
  the target. Reported for context (it is v13's `carry_forward` baseline analogue).
- Metrics per fold AND aggregate: **MAE / RMSE / MAPE** + **directional accuracy**
  (up/down vs `y(origin)` reference — the same reference `evaluate.directional_accuracy`
  uses). Report per-fold, never only the mean.

## 4. Matched-horizon comparison vs v13 — HONEST statement
The comparison is **per-crop, same target date, same origins**:
- v13 predicts `AvgPrice` at `ObservationDate + gp` (its harvest label). Prophet, at
  origin `t0 = ObservationDate`, forecasts `y(t0 + gp)`. **Same quantity, same horizon,
  same actual `y_true`** → MAE is directly comparable *for that crop*.
- To compare, recompute v13's error on **only this crop's rows**, on the **same origin/
  target dates** used for Prophet, and put three numbers side by side: Prophet MAE,
  seasonal-naive MAE, v13-on-this-crop MAE (+ dir-acc for each).

**Incomparability caveats (state these in the report, do not hide):**
1. **Headline mismatch:** v13's published CV MAE (100.31) is POOLED over 19 gated
   crops across folds. Prophet's number is ONE crop. They are NOT comparable. Only the
   *per-crop recomputed* v13 MAE is a fair opponent.
2. **Different information sets:** v13 uses lags/rollings/festival/macro/spread features
   + cross-crop pooling; Prophet uses univariate price history + holidays only. This is
   "which predictor wins at the same target," not "same features." Legitimate but note it.
3. **Different fit paradigm & fold geometry:** v13 CV splits on the pooled frame's
   observation dates; Prophet folds are per-crop on the daily series. We force the SAME
   origin/target dates for the head-to-head, but v13's *training* set still differs
   (pooled, purged on label date). Flag that the two models saw different training rows
   even at matched origins.
4. **Series-splice risk** (Section 1 caveat) affects both, but v13 already trained
   through it; Prophet sees it cold.

**Ship gate (later step):** at h = gp, Prophet must beat **BOTH** seasonal-naive AND
the per-crop v13 number on walk-forward, on BOTH crops, to be worth anything. Honest
expectation: **it likely loses** — a valid, reportable outcome. It ships nothing in this
spike regardless.

## 5. Leakage guards
- **Strict past-only fit:** Prophet trains only on rows with `ds < origin` (per-origin
  refit) — no row at or after the origin ever enters the fit. Forecast target `origin+gp`
  is always in the future relative to the training cut.
- **Holidays are future-known by nature — legitimately usable.** Festival dates come
  from a gazette/astronomical calendar fixed years ahead (`FestivalCalendarEntries`
  spans 2015-2030). Knowing "Avurudu = 2026-04-14" while standing at a 2025 origin is
  public deterministic knowledge, NOT lookahead — exactly the P2 harvest-date-anchored-
  festival rationale and precisely how Prophet's native `holidays` frame is meant to be
  used. Provisional future rows (`IsProvisional`, 12 rows) are passed through untouched;
  all CV origins are historical so provisional future dates never affect a fold's fit.
- No scaler/encoder is fit on full data (Prophet has none of that; trend/seasonality
  params are fit per fold on past-only rows).

## 6. Determinism
- `numpy` seed = **42**; Prophet `fit(..., seed=42)`.
- Estimation = MAP (`mcmc_samples=0`), algorithm **LBFGS** (Prophet default; Newton is
  its internal fallback if LBFGS diverges — if the fallback fires, record it per fold).
- **Hard Stan iteration cap = 10000** L-BFGS iterations, passed explicitly to the
  optimizer (`fit(..., iter=10000)`); a converged MAP typically needs far fewer, but the
  cap guarantees bounded, reproducible wall time. No MCMC path is ever taken.
- Environment note: prophet 1.1.7 ships a *stripped* bundled cmdstan (no top-level
  `makefile`) that cmdstanpy 1.3.0 rejects on validation; the spike sets
  `CMDSTAN=<prophet>/stan_model/cmdstan-2.33.1` and touches an empty `makefile` there
  (venv-local; the model binary is precompiled so `make` is never invoked). See the
  Step-1 report's BLOCKER — this must be resolved before Step 2.

## 7. Environment / artifact policy
- `experiments/` is **NOT gitignored** — files here WOULD be committed. This step
  commits nothing. When later steps commit, only `PLAN.md` + scratch scripts under
  `experiments/prophet_spike/` are candidates; any fitted Prophet artifact stays local
  (models/ is gitignored) and, if ever shipped, is serialized via `model_to_json` inside
  the single signed payload (CONTRACTS.md 2026-07-04), never a pickle side-car.
- Spike deps stay **venv-local**; `requirements.txt` is untouched until a ship decision.
