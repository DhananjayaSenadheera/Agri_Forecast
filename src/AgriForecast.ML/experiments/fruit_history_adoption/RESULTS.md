# v15 ADOPTION gate — extended HARTI-Pettah history for Ambul + Seeni

Seed 42. v13-faithful purged walk-forward, fold blocks FIXED from arm A (control)
and reused for arm B (v15), matched origins. All in-memory; production
`CropFeatureDaily` untouched. Raw numbers in `results.json`,
`per_row_{A,B}.csv`. Regenerate: `PYTHONPATH=. ./.venv/bin/python
experiments/fruit_history_adoption/run_gate.py`.

- **Arm A** = re-scored v13 incumbent on the CURRENT frame.
- **Arm B (v15)** = the SAME frame + the two adopted crops' pre-splice HARTI-Pettah
  history (Ambul FRT000003, Seeni FRT000006; 2,173 rows each, 2015-06-22 →
  2025-05-02). Built via the production backfill builder
  (`harti.fruit_history_backfill`) → row-identical to the fruit_history
  experiment's arm B for these two crops (proven in
  `tests/test_fruit_history_backfill.py`).
- Kolikuttu (FRT000005) NOT prepended (REJECTED); Papaya excluded (NULL gp).

## VERDICT: PROMOTE-READY (all substantive gates pass) — 2 documented flags

Adoption is unambiguously best for forecasting: Ambul 144.60→26.19 and
Seeni 152.16→18.93 matched DEC-era MAE, non-adopt cost ≈ 0, Kolikuttu unharmed.
Two honest flags below need owner acknowledgment; neither blocks and nothing
ships in this step.

| Gate | Result |
|------|--------|
| 2 — adopted crops beat incumbent + naive baselines | **PASS** (both beat A, carry, seasonal-naive) |
| 3 — non-adopt cost ≤ +1.0% AND net error change clearly negative | **PASS** (−0.033%, net −74,816) |
| 4 — no top-20 non-adopt crop degrades >5% | **PASS** (0/20 breach) |
| 5 — Kolikuttu unaffected | **PASS (substantive)**: series byte-identical, MAE 35.67→33.62; predictions NOT byte-identical (see flag 2) |

## Gate 1 — pooled CV MAE (v15 vs re-scored v13)
| | MAE | n |
|--|----:|--:|
| A_all (incumbent, current frame) | 115.89 | 34,541 |
| B_all (v15) | 111.34 | 35,633 |
| matched A (identical rows) | 115.89 | 34,541 |
| matched B (identical rows) | 113.72 | 34,541 |

On identical rows B beats A by 2.17 MAE. (The 100.31 v13 headline is from a
different, un-widened frame and is NOT comparable — never cite it here.)

## Gate 2 — matched DEC-era per-adopted-crop MAE
| Crop | gp | rows | A (incumbent) | **B (v15)** | recmean¹ | carry | seas-naive² | best baseline | B beats A? | B beats baseline? |
|------|---:|----:|-----:|-----:|-----:|-----:|-----:|-----:|:--:|:--:|
| **Ambul** | 90 | 315 | 144.60 | **26.19** | 144.60 | 30.04 | 31.02 | 30.04 | YES | **YES** |
| **Seeni** | 135 | 272 | 152.16 | **18.93** | 152.16 | 22.37 | 23.49 | 22.37 | YES | **YES** |

¹ Incumbent (arm-A) recency-mean = what production serves today (thin → poor).
² Seasonal-naive on the incumbent frame has low coverage for these crops (no
pre-2025 history without adoption): Ambul 64/315, Seeni 63/272 — the MAE is over
the covered rows only. Both crops still beat carry-forward decisively.

Reproduces the experiment (Ambul 26.08, Seeni 19.18) within noise — the small
delta is because v15 pools only 2 fruits vs the experiment's 3. **Not materially
different.**

**FLAG 1 — Ambul edged by an idealized same-data recency mean.** A
recency-weighted crop mean computed on the SAME extended frame gives Ambul 24.38
(< model 26.19) and Seeni 32.51 (> model 18.93). So Ambul's model does not beat a
same-data recency mean, though it beats every baseline production actually has
(incumbent 144.60, carry 30.04, seasonal-naive 31.02) and Seeni wins outright.
The hybrid serves crops with ≥365 rows via the MODEL, not recency mean, so 24.38
is not a served option today. Candidate future refinement: keep Ambul on
recency-mean serving even after adoption. Adoption remains a large net win for
both crops.

## Gate 3 — non-adopt cost (like-for-like) + net weighted error
| | MAE | n |
|--|----:|--:|
| non-adopt A | 115.33 | 33,954 |
| non-adopt B | 115.29 | 33,954 |
| **degradation** | **−0.033 %** | — |

- Net absolute-error change over ALL matched rows (Σ\|err_B\| − Σ\|err_A\|):
  **−74,816** (clearly negative → net improvement).
- Threshold: ≤ +1.0 % non-adopt degradation AND net change < 0 → **BOTH PASS.**
- Re-measurement note: the fruit experiment reported +0.6 % non-adopt cost with
  all 3 fruits pooled. Excluding Kolikuttu, the cost collapses to ≈ 0 (−0.033 %).

## Gate 4 — top-20 non-adopt crops by labelled-row volume
0 / 20 breach the 5 % degradation guard. Worst degradation +3.75 %; several crops
improve (best −6.36 %). Full per-crop table in `results.json`
(`gate4_top20_guard`).

## Gate 5 — Kolikuttu parity
| | value |
|--|------:|
| series byte-identical | **True** |
| never model-served (both arms) | **True** |
| MAE A → B | 35.667 → 33.623 (**−5.73 %**, improves) |
| predictions byte-identical | **False** |
| max prediction drift | 7.33 |

Kolikuttu's SERIES is byte-identical (the backfill writes only Ambul/Seeni rows —
the real adoption-safety property, also proven in the Part-1 parity tests). Its
predictions are NOT byte-identical because it is recency-mean fallback-served and
`recency_weighted_crop_mean_pred`'s GLOBAL prior (`overall`) shifts down when the
two fruits' low-nominal pre-splice labels enter the pooled train frame — the same
shared-prior coupling behind the (negligible) gate-3 perturbation. This is
architecturally UNAVOIDABLE whenever any crop joins the pool.

**FLAG 2 — Kolikuttu predictions not byte-identical, but unharmed.** MAE actually
improves (35.67 → 33.62); max drift 7.33 on a ~230 price. The substantive intent
(no adverse spillover to the rejected crop) holds.

## Directional accuracy (reporting)
Pooled A 0.6772 vs B 0.6766 (flat).

## Production wiring (Part 1) — DESIGN CHOICE: one-time backfill (a)
`backfill_fruit_history.py` / `harti.fruit_history_backfill` writes `Source='HARTI'`
MarketPrices rows (ExternalProductId −26 Ambul / −27 Seeni; EconomicCenterId NULL)
for `PriceDate < 2025-05-05`. `load_prices()` reads all MarketPrices and applies the
existing splice+dedup, so the extended series is served automatically with NO
hot-path change — same mechanism the 6 vegetable HARTI rows already use.
DEC data for these crops starts 2025-05-05, so there is no (CropId, PriceDate)
overlap; the splice invariant holds. Idempotent (keyed (CropId, PriceDate, Source)),
re-run inserts 0.

**Rejected (b):** union pre-splice history from PriceObservations inside
`load_prices()` at read time — puts a permanent per-crop special-case + a second-
table join on the hot path and splits the national series across two source tables.
Static backfill is simpler, deterministic, and never re-runs.
