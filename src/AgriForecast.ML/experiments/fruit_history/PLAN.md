# Fruit-history GATED experiment (Step 3 of the owner-approved fruit plan)

**Question:** Does extending the 4 fruits' training history with the newly
backfilled HARTI (Pettah wholesale) observations produce a *better* model, or a
better cold-start fallback distribution — enough to ship?

**Status:** EXPERIMENT. No commits to production, no production-DB/table writes,
no `promoted.json` move, no serving change. All frames built in-memory from a
modified `load_prices()` output; the production `CropFeatureDaily` store is never
touched. Artifacts live only under `experiments/fruit_history/`.

## The 4 fruit crops
| Crop | CropCode | CropId | gp (days) | In ML label experiment? |
|------|----------|--------|-----------|-------------------------|
| Banana – Abul (Ambul) | FRT000003 | bc018387… | 90 | yes |
| Banana – Kolikuttu    | FRT000005 | 02dc3010… | 120 | yes |
| Banana – Sini (Seeni) | FRT000006 | 83f194dd… | 135 | yes |
| Papaya                | FRT000018 | 75486680… | **NULL (perennial)** | **NO** — no gp ⇒ no harvest label ⇒ excluded from training by construction (Step-5 continuous-crop lockout). History still prepended to the price frame, but it contributes **0 labelled rows** and therefore cannot affect the ML label metrics NOR the (labelled-row-based) category fallback quantiles. |

Category of all 4 = DB `category_code = FRT` ("Fruit", 25 crops). This is the
single flat category `category_for()` returns, i.e. the `cold_start_category`
prior Kolikuttu's UI forecast uses today (there is no separate "banana"
subcategory in the DB).

## Data facts (verified against the live DB, read-only)
- National training series (`MarketPrices` via `load_prices()`): fruits exist
  ONLY as `DAMBULLA_DEC`, 394–397 rows each, from **2025-05-05** → 2026-07-10.
- HARTI backfill lives in `PriceObservations` (Source='HARTI', IsUnitConfirmed=1).
  Dominant market **Pettah (HARTI wholesale)**: Ambul 2457 / Seeni 2457 /
  Papaya 2457 from **2015-06-22**; Kolikuttu 1333 from 2020-06-24.
- KNOWN LEVEL GAP: Pettah (Colombo) wholesale ≈ 15–25 % above Dambulla for the
  same fruit/week ⇒ a raw splice has a systematic level break at 2025-05-05.
  This is exactly why the experiment is gated.

## Challenger series construction (in-memory only)
For the 4 fruit CropIds, prepend HARTI **Pettah** per-kg observations
(`AvgPrice=(Min+Max)/2`, dedup to one value/(crop,date)) as the national series
for dates **< 2025-05-05**; DEC rows unchanged from 2025-05-05 onward (no
overlap ⇒ no dedup collision). Every other crop is byte-identical to control.
Mirrors the veg convention (HARTI pre-splice + DEC post-splice).

## Arms (matched folds/origins, seed 42)
- **A (control):** current production history — fruits = DEC-only.
- **B (challenger):** raw extended-history splice.
- **C (optional):** only if B shows promise but the splice break visibly hurts —
  B + a per-crop `SourceIsHarti` splice-dummy feature (NO fancy re-scaling). If
  raw+dummy both lose, report the loss honestly.

## Method (faithful to v13, no invention)
- Both arms built with `features.build_all` on the SAME shared national signals
  (weather/fx/sentiment/policy/festivals/macro/price_obs), differing ONLY in the
  fruit price rows. `build_xy` = the production feature contract.
- **Fold blocks fixed from arm A** (production reference) and reused verbatim for
  B ⇒ identical test-origin windows. Fold construction copied from
  `purged_walk_forward`: expanding window, test region = last 40 % of unique obs
  dates split into 3 blocks; purge = drop train rows whose harvest date ≥ test
  start. Extra pre-2025 fruit rows in B fall before the test region ⇒ they become
  TRAIN data (the mechanism under test); fruit TEST rows stay matched (DEC-era).
- Predictor = the **v13 hybrid** (the promoted incumbent): pooled XGBoost
  (`make_model(0.5)`, log1p target, seed 42) fit on history-gated crops
  (≥365 labelled train rows, point-in-time) + `recency_weighted_crop_mean_pred`
  fallback on thin crops. No v14 recency-weighting (v13 stays promoted). Arm A
  run through this identical harness IS the honestly re-scored incumbent (per the
  v14 lesson: never compare a new frame against the stale 100.31 headline).

## Metrics (report ALL — no cherry-picking)
1. Pooled CV MAE A vs B; plus pooled MAE restricted to NON-fruit crops (must be
   ~identical — any change traces only to the pooled model being retrained with
   fruit rows, since non-fruit features/test-rows are byte-identical).
2. Per-fruit-crop CV MAE at matched horizons (h=gp), A vs B, each vs the best
   naive baseline: recency-mean AND seasonal-naive y(t−365) (matched origins).
3. Fold-level breakdown for the fruits (does pre/post-splice-origin behaviour
   differ).
4. Caveat quantification: how many fruit CV test origins have labels; how many
   test/train rows actually straddle the 2025-05-05 splice.
5. Cold-start `by_category` (FRT) p10/p50/p90 shift A vs B — evaluated exactly the
   way `_crop_fallback` derives it (quantiles of `LabelHarvestPrice` pooled over
   labelled rows of FRT-category crops).

## SHIP GATE (explicit verdict required)
B (or C) ships for the fruit crops ONLY IF it beats BOTH the best naive baseline
AND re-scored-A, WITHOUT degrading pooled MAE. Partial outcomes (helps some,
hurts others) are valid — report per-crop; propose per-crop adoption only if the
mechanism supports it. An honest ALL-LOSE is a valid outcome. Do not torture the
data. The category-fallback shift is reported separately: even on an ML-gate
fail, a better-informed FRT prior may be an independently shippable win.

Seed 42 everywhere. Determinism: fold blocks + XGBoost seed fixed.
