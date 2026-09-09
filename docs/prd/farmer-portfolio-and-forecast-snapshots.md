# PRD — Farmer Portfolio & Forecast Snapshots

**Status:** DRAFT for owner review — NOT approved to build.
**Date:** 2026-07-22
**Authors:** synthesized from three specialist analyses (UX/frontend, .NET backend, ML) grounded in the live codebases.
**Standing rule (owner, 2026-07-22):** this feature is committed. Any *other* modification shipped before it must not collide with the Reserved Design Space in §9.

---

## 1. Summary

Turn AgriForecast from a lookup tool into each farmer's daily companion, and give the system a permanent, honest record of its own forecasting accuracy.

Two intertwined deliverables:

1. **Forecast snapshots (Phase 0, backbone):** every night, record one prediction per crop ("if planted today, price at harvest will be X"). When the harvest date passes, fill in the actual price and the error. Feeds (a) a new admin **Forecast accuracy** tab in the Logs hub and (b) the portfolio's prediction history.
2. **Farmer portfolio (Phases 1–3):** each farmer builds a watchlist of crops with one preferred "home market", sees prices/trends/volatility/predictions for *their* crops, records their sales (lightweight self-report, no document upload), and finally sees the three-way comparison: **predicted vs market actual vs what they got**.

### Goals
- Farmers get a personal, mobile-first dashboard for the crops they actually grow.
- The owner gets a prospective, frame-invariant accuracy record per model version — the honest cross-version comparator the promotion guardrail currently lacks (fixes the v14→v17 cross-frame scoring gap).
- Trust: predictions are written down *before* the future happens; nobody can argue later about what the model claimed.

### Non-goals (explicitly out of scope)
- Per-market predictions (the model serves ONE Dambulla-anchored national price; market-as-label is the parked "P5" project).
- Invoice/document upload or OCR — sales are typed self-reports.
- Using any farmer-entered data in ML training (prohibited, §3).
- Payments, marketplace, or farmer-to-farmer features.

---

## 2. Phasing

| Phase | Deliverable | Depends on | Farmer-visible? |
|---|---|---|---|
| **0** | ForecastSnapshots table + nightly snapshot/mature job + admin "Forecast accuracy" tab | nothing | No (admin only) |
| **1** | Watchlist + home market + portfolio dashboard | Phase 0 (for prediction chips; degradable) | Yes |
| **2** | Sales log (record + list) | Phase 1 | Yes |
| **3** | Three-way comparison (predicted vs market actual vs sold) | Phases 0 + 2, and matured snapshots existing | Yes |

Phase 0 should be built **first** even if the portfolio waits: snapshots only gain value with time (a row matures ~2–3 months after it is taken), so every week of delay is a week of missing history.

---

## 3. Hard rules (laws, not preferences)

1. **Leakage quarantine — user data never trains the model.**
   - `UserSales` is a standalone table. Nothing is ever copied into `PriceObservations`/`MarketPrices`; no view/computed column/FK exposes `PricePerKg` to the feature layer; Python `load.py` never reads it (deny-listed in CONTRACTS).
   - `ForecastSnapshots` is a **write-only serving artifact**: `load.py`/`features.py` must never read it (a snapshot fed back into training is a self-referential lookahead). Static guard tests on both sides.
2. **Frozen predictions.** The snapshot pass writes prediction columns once. Maturing only *adds* actual/error columns — it never rewrites p50/p10/p90/confidence/activePredictor/modelVersion. No re-predicting with hindsight.
3. **Series identity for "actual".** The matured actual is the exact series the training label is a shift of: `CropFeatureDaily.AvgPrice` (Dambulla-anchored, HARTI↔DEC spliced), nearest trading day ≤ harvestDate within 5 days (= the label's `_FFILL_LIMIT`). Never a raw market average, never a symmetric window, never re-implemented ffill.
4. **Model vs fallback never blend.** Accuracy aggregates are always split by `activePredictor`; a combined "accuracy" number is prohibited. All aggregates groupable per `modelVersion`; rows keep the version that made them, never retroactively re-attributed.
5. **Farmer framing law.** A sold price below market actual is INFORMATION ("Market was Rs 185/kg that day. You sold at Rs 170/kg — Rs 15 lower."), never "error"/"loss"/"miss"/failure language. No RED anywhere in comparisons (red is reserved app-wide for "Not recommended"). Prediction-vs-actual gaps are neutral context, never "the model failed".
6. **Honest market labelling.** Home-market prioritisation re-points displayed prices/trends ONLY. Any prediction shown under a non-economic-centre home market carries a "National forecast" label.
7. **Fail-soft, report-only.** The snapshot/mature job never gates ingest/verify/train (gap_report tier philosophy). Per-crop failures are counted, not fatal. Portfolio UI treats readiness/prediction fetches as decoration (existing fail-soft pattern).
8. **Accessibility laws (existing, apply to all new UI):** colour never alone (glyph + word); red reserved for "Not recommended"; badges inside named controls use aria-hidden + aria-describedby → sr-only sibling (ReadinessBadge pattern); every chart ships a `<details>` table alternative; `ymdLocal` for all dates (UTC+5:30 trap); all new strings get en + si + ta keys (si/ta = owner task).

---

## 4. Phase 0 — Forecast snapshots

### 4.1 ForecastSnapshots table (reconciled schema)

.NET owns the migration/schema; **Python writes** (insert at snapshot, update at maturity); .NET reads (admin API + portfolio). Precedent: `ModelTrainingRuns`.

| Column | Type | Null | Notes |
|---|---|---|---|
| Id | uniqueidentifier | no | |
| CropId | uniqueidentifier FK→Crops (Restrict) | no | lowercase-normalised on the wire |
| SnapshotDate | date | no | = plantDate = the pass's as-of day |
| HarvestDate | date | yes | SnapshotDate + GrowthPeriodDays from the /predict payload (NOT the Crop column); null when gp unresolvable |
| GrowthPeriodDays | int | yes | as served |
| PredictedPrice | decimal(10,2) | no | p50, verbatim |
| LowerBound / UpperBound | decimal(10,2) | no | p10 / p90 (nominal 80% band) |
| ReferencePrice | decimal(10,2) | yes | AvgPrice known at plantDate (carry-forward anchor) — stored at snapshot time so directional accuracy is computable later without re-reading history |
| Confidence | nvarchar(20) | no | Low/Medium/High verbatim — never upgraded |
| ActivePredictor | nvarchar(50) | no | e.g. residual / crop_mean_fallback |
| FallbackTier | nvarchar(50) | yes | as served |
| ModelVersion | nvarchar(20) | yes | e.g. v17 |
| ReasonCode | nvarchar(100) | yes | verbatim |
| MaturityState | nvarchar(30) | no | pending / matured / actual_unavailable / not_maturable |
| ActualPrice | decimal(10,2) | yes | filled at maturity |
| ActualObservedDate | date | yes | the trading day actually used (audits the ≤5-day carry) |
| SignedError | decimal(10,2) | yes | p50 − actual |
| AbsoluteError | decimal(10,2) | yes | |
| PercentageError | decimal(9,4) | yes | signed |
| WithinInterval | bit | yes | p10 ≤ actual ≤ p90 |
| CreatedAtUtc | datetime2 | no | |
| MaturedAtUtc | datetime2 | yes | |

Indexes: **UNIQUE (CropId, SnapshotDate)** (idempotent nightly upsert — one row per crop per day; reconciled decision: ModelVersion is NOT part of the key, a day belongs to whichever version served it); non-unique (HarvestDate) filtered `WHERE MaturityState='pending'` (maturing sweep hot path); non-unique (CropId, SnapshotDate DESC) (dashboard "latest per crop").

### 4.2 Nightly job (Python-owned, .NET-triggered)

- **ML endpoint (reserved):** `POST /admin/snapshot-forecasts` (X-API-Key via existing admin_router). Request `{ "snapshotDate"?: "YYYY-MM-DD", "runMature"?: true, "dryRun"?: false }`. Response is the summary-dict style (reconciled — the .NET mirror adopts this richer shape):
  ```json
  { "status": "ok",
    "snapshot": { "snapshotDate": "...", "cropsAttempted": 96, "inserted": 96, "updated": 0,
                  "modelServed": 11, "fallbackServed": 85, "notMaturable": 4, "modelVersion": "v17" },
    "mature":   { "scanned": 412, "matured": 47, "stillPending": 360,
                  "markedUnavailable": 5, "maxHarvestDateMatured": "..." },
    "errors":   { "snapshotCropFailures": 0, "matureRowFailures": 0 } }
  ```
- **Snapshot pass:** for every crop call the existing `predict_harvest(cropId, plantDate=snapshotDate)`; MERGE-upsert on (CropId, SnapshotDate). Crops with unresolvable growth period are still recorded, `MaturityState='not_maturable'` (terminal, excluded from aggregates).
- **Mature pass:** scan `MaturityState='pending' AND HarvestDate <= today`. Actual = nearest trading `CropFeatureDaily.AvgPrice` ≤ harvestDate within **SNAPSHOT_MATCH_BACK_DAYS=5** (== `_FFILL_LIMIT`; factor a shared `_last_avgprice_at_or_before()` helper reused by `_carry_forward_price` so serve/mature can't drift). Ingest look-back means the price may land late: retry nightly; after **harvestDate + SNAPSHOT_UNAVAILABLE_AFTER_DAYS=14** with no match → terminal `actual_unavailable` (counted, surfaced, never silently dropped, never faked). Grace constant SNAPSHOT_MATURE_GRACE_DAYS=7 documents the latency rationale.
- **.NET trigger:** `ForecastSnapshotIngestionService` — exact clone of the CBSL ingestion service shape. SourceKey **`FORECAST_SNAPSHOT`** (added to `IngestionSources.KnownKeys`), feature flag **`ForecastSnapshots:Enabled`** (OFF ⇒ Disabled watermark, reported Skipped, never a failure), typed HttpClient on `MlService:BaseUrl` + `MlService:AdminApiKey`, watermark LastObservedDate = last snapshot date, RecordFailure on any error (resume point not advanced), wrapped in `IngestionRunAudit.RunTrackedAsync`.
  **CORRECTED 2026-07-27 (review finding on PR 0b):** the trigger must run **after `build_features`** (pipeline step [6]), NOT "last in the Worker pass" as originally written. The Worker is init-container [1]; `CropFeatureDaily` — which both the snapshot anchor and the mature pass read — is only rebuilt at step [6], so a Worker-pass trigger would predict and score against a feature store one pipeline-day stale (measured median day-over-day |Δ| in AvgPrice ≈ 10%). The original parenthetical ("predicts against that night's freshly-ingested prices") was true of `MarketPrices` but false of `CropFeatureDaily`. PR 0c therefore invokes the snapshot step from the daily pipeline **after** the feature build (fail-soft: a failed build skips that night's snapshot advance; pending rows catch up the next night), while keeping the `FORECAST_SNAPSHOT` run-audit row and `ForecastSnapshots:Enabled` flag for admin visibility. The Python mature pass is additionally ordering-independent (exact-day-or-wait-out-grace rule) so a mis-placed trigger can no longer corrupt the ledger.
- **Storage:** ~96 crops × 365 ≈ 35k rows/yr ≈ ~9 MB/yr. Negligible.

### 4.3 Accuracy metrics (reuse `train/evaluate.py`, do not re-derive)

Per-row at maturity: signedError, absError, pctError (same 1e-6 clip as `regression_metrics`), withinInterval.
Aggregates (windowed/filtered): MAPE, **medianAPE** (robust headline), **intervalCoverage vs nominal 0.80** (report the gap: ≪0.80 = overconfident band, ≫0.80 = too wide), signedBias, directionalAccuracy (via `evaluate.directional_accuracy`, reference = stored ReferencePrice — plantDate data only). All split model-vs-fallback (mandatory) and groupable per modelVersion.

### 4.4 Admin read surface

- Controller `AdminForecastAccuracyController`, route **`api/admin/forecast-accuracy`**, Admin-only.
  - `GET /summary` — matured/open counts, aggregates per activePredictor and per modelVersion, latest snapshot date. UTC-stamped datetimes (`AsUtc` helper).
  - `GET /snapshots?page&pageSize&cropId?&modelVersion?&maturedOnly?` — server-paged, newest first, empty page = 200 with empty items. Validators: page ≥ 1, pageSize ∈ [1,100].
- Application area `Requests/Admin/ForecastAccuracy/` (correct `Queries/` spelling), read-store seam `IForecastAccuracyReadStore` (AsNoTracking), house DTO naming.
- **FE:** 5th tab **"Forecast accuracy"** in the admin Logs hub, following the existing tab patterns; i18n namespace `admin.forecastAccuracy.*`.

### 4.5 Delivered `/summary` breakdowns (A4, 2026-09)

Recorded here because the delivered shape settles four points the specs left open (one per bullet below); code of record: `ForecastAccuracyMath` + `GetForecastAccuracySummaryQueryHandler`.

- **Horizon buckets** `byHorizonBucket`: matured rows bucketed by served growth period — short < 60 days, medium 60–120 inclusive, long > 120, plus `unknown` only when occupied — **keyed by (activePredictor, bucket)**. The survivorship disclosure: a short window can only contain fast-maturing crops, and pooling horizons hid that inside the average.
- **Macro (per-crop-weighted) averages** `macroMape`/`macroMedianApe` ride on **every predictor-keyed metrics object**, NOT as a top-level figure. Deliberate deviation from the task wording "at the summary level": a top-level macro would pool model- and fallback-served rows and violate law §3-4. Same split-law reasoning as everything else on this surface.
- **Worst-crops triage list** `worstCrops`: up to 10 entries **per predictor** (each predictor contributes up to 10 of its own worst crops; the concatenation is re-sorted worst-first for display — a single global cap let one predictor's bad crops starve the other's off the list), **keyed by (activePredictor, cropId)** (law §3-4 — an entry pooling a crop's predictors is a blended accuracy number), ranked worst-first by the pair's medianApe over **non-copy scored rows only** (copies — prediction value-equal to the carry-forward anchor — have APE ≈ 0 and would demote exactly the crops the list exists to surface). Qualification: ≥ `worstCropMinScoredCount` (5) non-copy rows; each entry disclosed with `scoredCount`/`copyCount` and a `meetsMinimumSample` badge against the 30-row metrics gate (small-sample entries are flagged, never hidden). A crop whose rows are all copies has no measured forecasts and cannot be ranked — with today's all-fallback-copy data the list is **empty by design**, with the threshold on the wire so the page can say why.
- **Minimum-sample gate**: threshold 30, applied **per denominator** — mape/medianApe/signedBias against `scoredCount`, the baseline trio against `baselineScoredCount`, `directionalAccuracy` against `directionalScored`, `intervalCoverage(+gap)` against `intervalScoredCount`. The macro pair alone has **two bars, rows and crops**: `scoredCount` ≥ 30 AND `distinctCropCount` ≥ 3 (`MinDistinctCropsForMacro` — a mean over fewer than 3 crops is not a "typical crop"; at 1 it duplicates micro, at 2 a single crop carries half the crop-weight). `meetsMinimumSample` reports the headline (`scoredCount`) decision only; every count always ships, so a masked figure renders as "n, below the 30-row minimum", never as a silent gap.

---

## 5. Phases 1–3 — Farmer portfolio

### 5.1 Information architecture (FE)

- **No 5th nav tab** — the documented 4-tab lock stands. Portfolio ships as a **non-tab route** (the `/best-crops/compare` precedent): reserved routes `/portfolio`, `/portfolio/settings`, `/portfolio/crop/:cropId`, `/portfolio/sales`, `/portfolio/sales/new`; entry points = a prominent card on Overview + a "My crops" link in the SessionMenu. Lazy-loaded chunk (admin-console precedent).
- **Option A (future, owner sign-off required):** repurpose the Overview tab into the personalised "My crops" home. Deliberate IA re-decision, not ad-hoc.
- Portfolio *links to*, never rebuilds: My harvest (`/my-harvest?crop=<id>` deep-link for full forecast), Prices (add a small FE-only `?market=` preselect), Best crops (gains an "Add to my crops" affordance).

### 5.2 Screens (all with loading/success/empty/error states; mobile-first, 44px+ targets)

- **`/portfolio` dashboard:** home-market banner ("Prices shown for: Dambulla ▾") + watchlist as responsive cards/table. Per crop: name + compact ReadinessBadge (aria-hidden + sr-only sibling), current home-market price, trend (glyph+word), price-swing indicator, prediction chip "≈ Rs X (Good/Fair/Low)" with **"National forecast"** label when home market ≠ economic centre. Two distinct empty states: no watchlist ("Add crops") vs watchlist-with-no-data ("No prices for your crops yet" — never a fake number). Fallback-served crops show the prediction visibly de-rated (Low), never hidden.
- **`/portfolio/settings`:** CropPicker multi-select (reused; trilingual search + readiness tint built-in) + home-market native select (economic centre default).
- **`/portfolio/crop/:cropId`:** home-market price chart (reuse PriceLineChart) + national ForecastResult (reuse/deep-link) + (P3) comparison card + "Record a sale".
- **`/portfolio/sales` + `/sales/new`:** newest-first grouped list; entry form fields = crop (prefilled in context) / market (home default) / date (native picker, max=today, `ymdLocal`) / price Rs/kg (`inputmode="decimal"`, forgiving parsing, `formatPrice` echo). Offline tolerance: failed POST buffers in the versioned `storage.ts` pattern ("Saved on this phone, will sync") and re-flushes.
- **Three-way comparison card (P3):** three aligned bars on ONE shared Rs/kg scale — Forecast (with p10–p90 band, never a bare point) / Market actual (range if min≠max) / Your sold price — plus the neutral plain-language sentence (§3.5). `<details>` table alternative. No red, ever.

### 5.3 Backend (all `[Authorize]`, user resolved from JWT only — never from body/route)

**Tables** (house factory-style entities, private setters):
- **`UserCropWatchlist`**: Id, UserId FK→Users (Cascade), CropId FK→Crops (Restrict), PreferredMarketId? FK→Markets (Restrict), CreatedAtUtc, UpdatedAtUtc. UNIQUE (UserId, CropId).
- **`UserSales`** ⚠ quarantined (§3.1): Id, UserId (Cascade), CropId (Restrict), MarketId? (Restrict), SaleDate date, PricePerKg decimal(10,2) > 0, QuantityKg? decimal(12,2), Note? nvarchar(500) (trim+cap), CreatedAtUtc, UpdatedAtUtc. Index (UserId, SaleDate DESC).

**Routes** (prefix reserved: `api/portfolio`):
| Route | Verb | Purpose |
|---|---|---|
| `/watchlist` | GET / POST / PUT `{cropId}` / DELETE `{cropId}` | manage watchlist + preferred market |
| `/dashboard` | GET | per watched crop: latest price/trend/volatility at preferred market (national fallback) + latest snapshot prediction |
| `/sales` | GET (paged) / POST / PUT `{id}` / DELETE `{id}` | sales CRUD, owner-scoped (miss ⇒ 404, never reveal others' rows) |
| `/sales/{id}/comparison` | GET | market actual (PriceObservations at sale market/date) vs prediction (snapshot whose HarvestDate covers SaleDate) vs sold price; each leg nullable with a reason |

Validators: pricePerKg > 0 with sane ceiling; saleDate not future; paging bounds. Cross-user isolation is a first-class test target.

### 5.4 Volatility — reconciled recommendation

Two honest candidates emerged; **decision defaults to (a), owner may override**:
- **(a) P1 ships "price swing" derived client-side** from the home market's observed `getPriceHistory` dispersion (steady/moderate/big, glyph+word, neutral colours). Honest about what's displayed (the farmer's chosen market), zero backend dependency.
- **(b) Later/optionally: server-side `volatility30d`** = rolling 30-day CV (ddof=1) on the national label series; null when n<2 or mean≤0, `thin=true` when 2≤n<8 (VOL_MIN_OBS=8). The canonical metric if volatility ever feeds anything beyond display.
Never mislabel `intervalWidthPct` (prediction-interval width) as market volatility.

### 5.5 i18n namespaces (reserved; si/ta = owner task at each PR)

`nav.portfolio`, `pages.portfolio.*`, `pages.portfolioSettings.*`, `pages.portfolioCrop.*`, `pages.sales.*`, `pages.compare3way.*`, `portfolio.swing.*`, `admin.forecastAccuracy.*`. Reuse without new keys: `confidence.*`, `verdict.*`, `crop.readyBadge/collectingBadge`, `common.*`, `forecast.*`, `pagination.*`.

---

## 6. Build plan (PR breakdown)

| PR | Repo | Contents | Gate |
|---|---|---|---|
| 0a | backend | `CreateForecastSnapshots` migration + entity (schema only) | — |
| 0b | backend (Py) | `snapshot_service` + `/admin/snapshot-forecasts` + maturing + hermetic tests | series-identity tests green |
| 0c | backend (.NET) | `ForecastSnapshotIngestionService` + Worker wiring + flag (ships `Enabled=false` until 0b live) | — |
| 0d | backend | `IForecastAccuracyReadStore` + admin API + validators | — |
| 0e | ForecastUI | "Forecast accuracy" Logs tab | — |
| 1a | backend | `CreateUserCropWatchlist` + watchlist/dashboard CQRS + PortfolioController | cross-user isolation tests |
| 1b | ForecastUI | `/portfolio` + `/portfolio/settings` + crop page (P1 scope) | a11y exact-name tests |
| 2 | backend + FE | `CreateUserSales` (quarantined) + sales CRUD + sales UI | `PortfolioLeakageTests` green |
| 3 | backend + FE | comparison query + ThreeWayCompareCard | framing-law test (no red/"error" tokens) |

Test baselines at time of writing: **.NET 671 · Python 1023/1 · FE 395** — every PR re-verifies. Migration hazard: branch only after any open PR touching `AgriForecastDbContextModelSnapshot.cs` merges.

---

## 7. Test plan highlights

- **ML (hermetic, mocked-engine):** series identity (actual == label semantics); gap-window edges (match −3d over −6d; −6d-only ⇒ no match); pending→unavailable timing; snapshot idempotency; frozen-prediction immutability; model/fallback never blend; volatility degradation; static leakage guards (no loader/feature module mentions ForecastSnapshots/UserSales); per-crop failure isolation.
- **.NET:** entity factory guards; snapshot service flag-off Skipped / failure RecordFailure / watermark advance; accuracy paging+filters+UTC; watchlist/sales cross-user isolation (404 for non-owner); `PortfolioLeakageTests` (no navigation from ML-consumed entities into UserSales); auth wiring (accuracy Admin-only, portfolio Authorize).
- **FE:** priceSwing/salesLog/compare3way pure-lib tests; `ymdLocal` date handling; exact accessible-name tests for every new named control; framing test asserting comparison UI contains no red styling or error/loss vocabulary.

---

## 7.5 AMENDMENT — owner IA re-decision 2026-07-28 (pre-Phase-2, supersedes parts of §5)

Phase 1 shipped 2026-07-28 (backend PR #76, FE PR #48), then the owner re-decided the portfolio model before Phase 2:

1. **Per-crop watch markets replace the one-home-market invariant.** Each watched crop watches up to **3 markets** (owner-picked cap); watchlist caps at **10 crops**. `UserCropWatchlist.PreferredMarketId` is dropped in favour of child table `UserCropWatchMarkets`; dashboard's top-level `homeMarket` is removed. Economic-centre (Dambulla) fallback applies only to crops with zero watched markets. Per-market *predictions* still do not exist (§3 law unchanged) — markets affect displayed prices only.
2. **Markets gain a display-only `ShortCode`** (Dambulla = DEC per owner example; all 12 distinct; owner reviews proposed codes). Shown on card headers with full-name tooltip.
3. **Card redesign:** market short-code tabs (Logs-page style) each with that market's price + price chart; the "≈ Rs X at harvest" snapshot chip is REMOVED from the card body. In its place: a **planted-date section** — farmer records their real plant date (per crop, not per market), and the card then shows the forecast for THEIR planting (existing national forecast called with that plantDate) + link to the forecasting page. "See details" becomes "More details" → popup; Phase 2 management (sales, costs) will live in that popup (content TBD by owner).
4. **Top navbar app-wide**, alongside the existing 4-tab navigation (not replacing it): brand + username + sign-out top-right (ordering per HCI by the UI agent). The session controls move up from the nav drawer.
5. The `/portfolio/settings` management flow merges into `/portfolio` itself (watchlist cards on top, crop-selection table below, per the owner's sketch: tick crops + pick markets → "Add to watch list"; tick cards → remove; "watching X/10" counter).

Wire codes added: `watchlist_full` (11th crop), `too_many_markets` (4th market). The snapshot prediction leg stays in the dashboard payload (feeds the More-details popup).

## 8. Open decisions (owner)

1. **IA placement:** ship P1 as non-tab `/portfolio` (recommended, unblocks now) — with Option A (portfolio becomes the Overview home) as a possible later re-decision.
2. **Volatility:** FE-derived home-market "price swing" for P1 (recommended) vs building the server-side national `volatility30d` now.
3. **P1 persistence:** build the small watchlist backend first (recommended) vs localStorage-first with later migration.
4. **si/ta translations** for all new namespaces — owner supplies at each FE PR (standing arrangement).
5. **Day-one accuracy seeding:** leave the accuracy page to fill naturally (recommended) vs pre-filling with clearly-labelled retrospective backtest rows.

---

## 9. Reserved design space (collision checklist for ALL other work)

Any modification shipped before this feature must not use or repurpose:

- **Tables / DbSets:** `ForecastSnapshots`, `UserCropWatchlist`, `UserSales`.
- **API routes:** `api/admin/forecast-accuracy/*`, `api/portfolio/*`.
- **ML admin endpoint:** `POST /admin/snapshot-forecasts` (+ its payload keys, §4.2).
- **Ingestion source key:** `FORECAST_SNAPSHOT` (IngestionSources.KnownKeys + watermark row + admin runs filter).
- **Config keys:** `ForecastSnapshots:Enabled`, optional `MlService:*Snapshot*` (timeout/lookback).
- **Admin Logs hub:** the 5th tab slot + `admin.forecastAccuracy.*` i18n namespace.
- **FE routes:** `/portfolio`, `/portfolio/settings`, `/portfolio/crop/:cropId`, `/portfolio/sales`, `/portfolio/sales/new`; the Overview entry-card slot + SessionMenu "My crops" link slot; NO new nav tab.
- **FE names:** components `PortfolioPage`, `PortfolioSettingsPage`, `PortfolioCropPage`, `SalesLogPage`, `SalesEntryForm`, `WatchlistRow/Card`, `HomeMarketBanner`, `PriceSwingBadge`, `ThreeWayCompareCard`; libs `lib/portfolio.ts`, `lib/salesLog.ts`, `lib/priceSwing.ts`, `lib/compare3way.ts`; `styles/portfolio.css`; storage key prefix `agriforecast.portfolio*`.
- **i18n namespaces:** §5.5 list.
- **ML constants:** `SNAPSHOT_MATCH_BACK_DAYS=5`, `SNAPSHOT_MATURE_GRACE_DAYS=7`, `SNAPSHOT_UNAVAILABLE_AFTER_DAYS=14`, `VOL_MIN_OBS=8`.
- **Application areas:** `Requests/Admin/ForecastAccuracy/`, `Requests/Portfolio/`.

Also: any change to `predict_harvest`'s response contract, `CropFeatureDaily`'s AvgPrice semantics, the `_FFILL_LIMIT` constant, or the HARTI↔DEC label splice **must be checked against §3.3/§4.2** — the maturing job's honesty depends on them.

---

## 10. Risks

| Risk | Mitigation |
|---|---|
| Accuracy page empty for ~2–3 months | Expected and communicated; Phase 0 built first so history accrues while other phases are developed |
| Self-reported sale prices poisoning trust in comparisons | Framing law (§3.5) + sales never leave the farmer's own view + never train |
| Label-series change (e.g. splice edits) silently invalidating matured rows | Reserved-contract note in §9; matured rows keep ActualObservedDate for audit; re-maturing is prohibited (frozen rows) |
| Model version churn muddying accuracy | Per-version attribution, never blended, never re-attributed |
| Nightly pass cost | One /predict call per crop (~96) + one indexed sweep — trivial |
