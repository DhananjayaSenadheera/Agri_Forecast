# News -> ML Features: Step 1 Data Audit + Feature Design (ANALYSIS ONLY)

Branch `feat/news-ml-features` @ 087a56d. Read-only DB audit, no production code changed, nothing committed.
Audit run 2026-07-16 against the live dev DB (pymssql). All numbers below are measured, not estimated.

## TL;DR verdict: **NO-GO for a gated v15 now** (GO to re-audit in ~4-6 months)
News history is ~2.5 weeks of dense coverage. **Zero labelled feature rows fall in the dense news window**,
so no CV fold can fairly test news signal today. The 4 sentiment columns are *already* in the store and
*already* in v13's feature set, but 98.4% NULL. Adding more news features now cannot move the gate; it can
only add noise columns. Revisit once >=12 months of dense daily coverage has accrued.

---

## A. NewsArticles audit
- Total rows: **424**. `PublishedDateUtc` null: 0 (every row has a publish date).
- `PublishedDateUtc` (effective/publish date) range: **2026-03-30 -> 2026-07-16**.
- `RetrievedAtUtc` (ingestion) range: **2026-06-30 -> 2026-07-16** (pipeline went live 2026-06-30; 11 ingestion runs since).
- Effective date = COALESCE(PublishedDateUtc, RetrievedAtUtc) == publish date here (no nulls).
- Rows per month (effective): 2026-03 = 1, 04 = 4, 05 = 10, **06 = 69, 07 = 340**. Real density only from late June.
- Per-feed: economy_next 137, island_lk 111, ada_derana_biz 92, lbo 45, reliefweb_lka 23, agrifarming_in 16.
- SentimentScore written back: **0** — the `NewsArticles.SentimentScore` debug column was never added (add_sentiment_score_column never ran). Per-article scores are computed in-memory only; the daily table is the sole persisted artifact.
- The only pre-June-30 signal comes from **reliefweb_lka** (first effective 2026-03-30) — back-dated disaster reports. The other 5 feeds all first appear at the 2026-06-30 go-live.

## B. NewsSentimentDaily audit
- Rows: **45**. Date range **2026-03-30 -> 2026-07-16**. Calendar span 109 days -> **41.3% day coverage** within its own range (sparse pre-June, near-daily in July). No duplicate days.
- Actual schema (15 cols): `Date(date PK), MeanSentiment(float), ArticleCount(int)`, then Count+Ratio pairs for **Pest, Flood, Drought, Policy, Fertiliser, ImportBan**.
- **Signal variance (there IS variance):** MeanSentiment mean 0.149, sd **0.535**, min -0.987, 25% -0.118, 50% 0.278, 75% 0.434, max 0.948; 45/45 unique, 0 exact zeros. Not flat.
- ArticleCount: mean 9.4, sd 12.5, median **2**, 25%ile **1**, max 36 -> bimodal: sparse 1-article back-dated days vs dense ~17-36-article July days.
- Per-topic day counts (days with count>0): **Policy 20 days / 47 articles** (only lively topic); Flood 6, Drought 3, Pest 1, Fertiliser 1, **ImportBan 0**. Drought/Flood ratios are ~always 0 -> near-constant columns.

## C. NewsEvents audit (admin table, API-12)
- Rows: **0**. NewsEventCrops: 0, NewsEventMarkets: 0. Schema present (Id, EventType, Direction, Title, Description, PublishedAt(date, vintage), SourceUrl, CreatedAtUtc) but **completely unpopulated** — no curator has entered events.
- => Any NewsEvents-derived feature is unbuildable now (nothing to join). Explicitly deferred.

## D. CRITICAL FEASIBILITY / overlap numbers
Labelled feature frame (CropFeatureDaily WHERE LabelAvailable=1): **58,545 rows**, obs range 2015-06-22 -> **2026-06-08** (store last built ~2026-06-08; the dense July news is NOT even in the store yet).
News as-of coverage window = [2026-03-30 .. 2026-07-16]; DENSE sub-window = [2026-06-30 .. 2026-07-16].

| Anchor | in full news window | in DENSE window |
|---|---|---|
| ObservationDate (where the feature attaches) | **940 rows = 1.606%** | **0 rows = 0.000%** |
| HarvestDate (label realizes gp days later) | 6,571 = 11.22% | 461 = 0.787% |

**The feature attaches at ObservationDate.** Store obs max (2026-06-08) predates dense news (2026-06-30), so **0 labelled rows sit in the dense window**. The 940 non-null sentiment rows are all sparse, single-article, largely reliefweb back-dated readings carried forward by the as-of join.

**Per-fold availability (v13 purged expanding-window, 3 folds; test_region = last 40% of 2,788 unique obs dates, starts 2022-12-30):**

| Fold | test dates | test rows | rows with any as-of news | dense-window rows |
|---|---|---|---|---|
| 1 | 2022-12-30 .. 2024-03-29 | 6,885 | **0 (0.00%)** | 0 |
| 2 | 2024-03-30 .. 2025-05-03 | 6,848 | **0 (0.00%)** | 0 |
| 3 | 2025-05-04 .. 2026-06-08 | 20,598 | 940 (**4.56%**) | 0 |

Folds 1 and 2 have the sentiment feature 100% NaN -> they cannot test news at all. Only fold 3 has any, and only 4.56% of it, none in the dense window. **A v15 challenger's CV MAE would be v13's MAE +/- noise on 940 fold-3 rows.** The gate cannot fairly decide.

**Already-in-store confirmation:** MeanSentiment, DroughtRatio, FloodRatio, PolicyRatio already exist as 4 of the 84 CropFeatureDaily columns and are NOT in dataset._EXCLUDE, so **v13 already trains on them** — but each is **57,605/58,545 = 98.4% NULL** (940 non-null). They are effectively dead columns today; XGBoost sees them as ~all-missing.

## E. Proposed feature design (SMALL set; DO NOT BUILD until overlap improves)
House style precedent = `features._attach_sentiment` (mirrors `_attach_fx`: backward `merge_asof` on ObservationDate, national/identical-across-crops, uncapped carry-forward, NaN when no prior reading). Publication-safe date column = **NewsSentimentDaily.Date** (= article effective date = COALESCE(PublishedDateUtc, RetrievedAtUtc)); the backward join (Date <= ObservationDate) is the leakage gate. Note the ingestion-lag caveat in G.

Proposed **net-new** columns (keep total single-digit; the 4 existing stay):

| Col | Source | As-of / leakage rule | Null policy pre-coverage | Rationale |
|---|---|---|---|---|
| `SentimentRoll7` | NewsSentimentDaily.MeanSentiment | mean of readings with Date in (D-7, D]; backward only | NaN (missing-indicator covers it) | denoises 1-article-day spikes; short-horizon mood |
| `SentimentRoll30` | same | mean of readings in (D-30, D] | NaN | medium-horizon mood; matches Prophet horizon band |
| `NewsStaleDays` | NewsSentimentDaily.Date | D - (latest Date <= D) | large sentinel / NaN | tells the tree how trustworthy the carried reading is |
| `NewsMissing` | derived | 1 if no Date <= D else 0 | 1 (pre-coverage) | house-style missing-indicator (mirrors macro NaN-means-not-knowable) |
| `PolicyRoll30` | NewsSentimentDaily.PolicyRatio | mean in (D-30, D] | NaN | Policy is the only lively topic; medium-horizon policy-noise proxy |

Drop candidates (do NOT add): DroughtRatio/FloodRatio/Pest/Fertiliser/ImportBan rolling variants — those topics fire 0-6 days total, they are constant-0 columns with no learnable variance. Every added col also needs an `explain._LABELS` entry (the existing 4 already have labels at explain.py:62-65).

NewsEvents features (`EventShockActive`, `EventNetDirection`, per-`affectedMarketIds` flags): **design deferred** — 0 rows to build from.

## F. GO / NO-GO
**NO-GO for building/gating a v15 news challenger now.** One-paragraph reason: the feature attaches at ObservationDate, and the store's newest labelled observation (2026-06-08) predates the dense news window (2026-06-30+), so **zero labelled rows carry a real multi-article news reading**; folds 1-2 are 100% NaN and fold 3 is 95.4% NaN with 0 dense rows. The 4 sentiment columns are already shipped inside v13 and already dead. Any measured MAE delta from adding features would be noise on <=940 sparse fold-3 rows, which would make the incumbent-beating gate dishonest. Honest middle path if the owner still wants motion: (1) keep the pipeline running to accrue history; (2) OPTIONAL low-cost step now = rebuild CropFeatureDaily so July obs enter the store (they will be unlabelled until harvest, so still no gateable rows) and add a **shock-day secondary report** (does |sentiment| spike align with next-gp-day price moves on the 45 covered days) as a diagnostic, NOT a gate. Re-audit for a real gate once >=~12 months of dense daily coverage exists AND labelled obs rows fall inside it (earliest realistic: ~mid-2027, since today's July obs only become labelled gp days later).

## G. Anomalies / risks
- **Ingestion-lag leakage nuance:** the 23 reliefweb rows are back-dated (publish Mar-May) but were only retrieved 2026-06-30. The as-of join treats them as knowable on their publish date. Defensible (info was public), and moot today (no labelled obs in the dense window), but if the store is ever rebuilt to include June/July, prefer gating on an ingestion-aware date (max(PublishedDateUtc, first RetrievedAtUtc-of-run)) or explicitly accept publish-date PIT for backtests. Flag in Step 2 if it proceeds.
- **Near-constant topic columns:** Drought/Flood/Pest/Fertiliser/ImportBan ratios are ~all zero -> no signal, risk of spurious splits. ImportBan literally never fires.
- **Possible feed die-off:** agrifarming_in last retrieved 2026-07-12 while all others reach 07-16 — either a stalled feed or just no new posts; watch it.
- **Store staleness:** CropFeatureDaily last built ~2026-06-08; it lags live news by ~5 weeks. Not a bug, but the audit's overlap is a lower bound that a rebuild only marginally improves (new rows are unlabelled).
- **No SentimentScore writeback** -> per-article scores are not auditable in the DB; only the aggregate survives.

## Files
- This file: `src/AgriForecast.ML/experiments/news_features/PLAN.md` (uncommitted).
