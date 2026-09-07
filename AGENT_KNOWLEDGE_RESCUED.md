# AgriForecast — Rescued Agent Knowledge

**What this is.** Nine project-specific agent definitions (`agri-backend-dev`, `agri-data-analyst`,
`agri-data-engineer`, `agri-dotnet`, `agri-ml-engineer`, `agri-qa`, `agri-reviewer`, `agri-security`,
`agri-uiux`) were retired on **2026-08-25** when the project moved to a portable agent fleet. Roughly half
their content was dated *project state* that existed nowhere else. This file preserves that half.

**Extracted:** 2026-08-25, by document-verifier, from
`~/.claude/agents-retired-20260825/*.md` (byte-identical to the copies then still live at `~/.claude/agents/`
and to a third set at `Project/Agri_Forecast/.claude/agents/` — all three verified identical by `diff -q`).

**How to read a citation.** `agri-ml-engineer.md:61` = line 61 of that retired definition. Every fact below
carries one. Nothing here has been re-verified against the code except where explicitly marked
"verified 2026-08-25".

**What was deliberately left behind:** generic craft (how to review a diff, how to write a pytest, what
leakage is in the abstract). That now lives in the portable fleet. What is kept is what only this project knows.

> ⚠️ **These notes are dated 2026-06-23 to 2026-07-09.** They were current when written. Treat every
> "status", "count" and "not yet built" claim as a *reading taken on that date*, not as today's truth.
> Re-verify before acting. Where the notes themselves disagree, see [§8 Open contradictions](#8-open-contradictions).

---

## 1. The real stack

### 1.1 The corrected reality (this is what to believe)

An identical "Real stack" paragraph, dated **2026-06-23**, appears in five files —
`agri-backend-dev.md:35`, `agri-data-engineer.md:34`, `agri-ml-engineer.md:40`, `agri-qa.md:36`,
`agri-reviewer.md:35`. Verbatim:

> **Real stack (supersedes any MLflow / 3-model-ensemble mentions above):** a **.NET 9 Clean-Architecture API**
> (crops, economic centers, market-price + weather ingestion) + a **separate Python FastAPI ML microservice**
> at `src/AgriForecast.ML` + **SQL Server**. The feature store is the **`CropFeatureDaily`** table (built by the
> Python feature pipeline). The model registry is a **lightweight file registry** — `models/<version>/model.pkl`
> + `metadata.json` + `promoted.json` — **NOT MLflow** (MLflow is only a future option). Current models:
> **Model A only** (pooled XGBoost); Prophet/LSTM are later phases.

A sixth, slightly different version dated **2026-07-01** is in `agri-security.md:46`, adding the DB location
and the ingestion sources:

> **Real stack:** a **.NET 9 Clean-Architecture API** + a **separate Python FastAPI ML microservice**
> (`src/AgriForecast.ML`) + **SQL Server** (Docker, `localhost,1434`). Model registry is a **file registry**
> (`models/<version>/model.pkl` + `metadata.json` + `promoted.json`), **not MLflow**. Ingestion scrapes
> government sources (HARTI PDFs, CBSL, Dambulla DEC REST API) daily.

The independent confirmation, from a grep run on **2026-07-03** (`agri-ml-engineer.md:61`):

> - Reality check (grep-confirmed 2026-07-03): **no Prophet/LSTM code exists** — pooled XGBoost (Model A) is
>   the only model; "3-model ensemble" is roadmap.

### 1.2 🔴 The contradiction inside these files — recorded, not resolved

The retired definitions **contradict themselves about their own stack**. The obsolete description was never
deleted; a correction was appended below it. Both readings, with locations:

| Reading | Where it is stated | Date |
|---|---|---|
| **MLflow is the registry / experiment tracker** | `agri-ml-engineer.md:3` (frontmatter `description`), `agri-ml-engineer.md:17`, `agri-ml-engineer.md:25`, `agri-backend-dev.md:3` (frontmatter `description`), `agri-backend-dev.md:12`, `agri-backend-dev.md:20`, `agri-reviewer.md:20` | undated (original definition) |
| **NOT MLflow — a file registry; MLflow is only a future option** | `agri-backend-dev.md:35`, `agri-data-engineer.md:34`, `agri-ml-engineer.md:40`, `agri-qa.md:36`, `agri-reviewer.md:35`, `agri-security.md:46` | 2026-06-23 / 2026-07-01 |
| **3-model ensemble routed by horizon: XGBoost short / Prophet medium / LSTM long** | `agri-ml-engineer.md:3`, `agri-ml-engineer.md:11-14`, `agri-backend-dev.md:12`, `agri-reviewer.md:18` | undated (original definition) |
| **Model A (pooled XGBoost) only; Prophet/LSTM are later phases; no Prophet/LSTM code exists** | the six "Real stack" paragraphs above, plus `agri-ml-engineer.md:61` | 2026-06-23 / 2026-07-03 |

**Which is newer and corrective:** the file-registry / Model-A-only reading. It is explicitly framed as a
supersession ("supersedes any MLflow / 3-model-ensemble mentions above"), it is dated, it is repeated
independently in six files, and it was grep-confirmed against the codebase on 2026-07-03. The MLflow /
3-model text is undated original boilerplate that was never edited out of the frontmatter.

**Why this matters and is not merely tidy-up:** the `description:` frontmatter is what an agent-routing layer
reads. So the *routing metadata* of two agents advertised a stack that the *body of the same file* says does
not exist. Anything downstream that consumed those descriptions (a fleet index, a router, a summary) may have
inherited the wrong stack. Search for it before trusting it.

**Not established here:** whether MLflow was ever installed and removed, or was only ever aspirational. The
files say "only a future option"; they do not say it was once real.

### 1.3 Stack facts, consolidated

- **.NET 9 Clean Architecture**, four layers, dependency rule Domain ← Application ← Infrastructure ← API;
  Domain has zero external dependencies — `agri-dotnet.md:11`.
- **CQRS via MediatR**; Commands/Queries as `IRequest<Result<T>>`; the `Result<T>` pattern with
  `Success` / `Failure` / `SuccessWithWarnings`; **FluentValidation** validators; **AutoMapper** profiles — `agri-dotnet.md:12`.
- **EF Core 9 + SQL Server**, `AgriForecastDbContext`, migrations via `dotnet ef migrations add` /
  `database update` — `agri-dotnet.md:13`.
- **Python FastAPI ML microservice**, run with `uvicorn`, `/health` check, models loaded once at startup — `agri-backend-dev.md:26`.
- **Frontend: React 18 + Vite 5**, plain CSS, **no router, no state library**, `npm run dev` on port **4173**;
  `src/api.js` is a single fetch helper; `VITE_API_BASE_URL` defaults to `http://localhost:5282` — `agri-uiux.md:12`.
- **SHAP** for explanations, **TreeExplainer-only** — `agri-backend-dev.md:69`.
- Password hashing = **ASP.NET Identity PBKDF2** — `agri-security.md:50`.

---

## 2. Repository layout and paths

### 2.1 Backend monorepo

- **Repo root:** `Projects/Agri_Forecast/Project/Agri_Forecast` — `agri-dotnet.md:31`.
- 🔴 **Retired location warning, verbatim** (`agri-dotnet.md:31`):
  > moved OUT of iCloud 2026-07-03 — the old curly-apostrophe `Documents - Dhananjaya’s Mac mini` location is
  > retired; if it or a straight-apostrophe twin reappears it is an iCloud artifact, not real work
- **How to prove you are in the real tree:** check `AgriForecast.API/Program.cs` exists — `agri-dotnet.md:31`.
- **Solution:** `src/src.sln` — 5 .NET projects plus an `AgriForecast.ML` solution folder holding the Python
  project, which is **not MSBuild-built** — `agri-dotnet.md:33`.
  *(Verified 2026-08-25: `src/` contains `AgriForecast.API`, `.Application`, `.Domain`, `.Infrastructure`,
  `.Ingestion`, `.Tests` = 5 projects + Tests, plus `AgriForecast.ML` and `src.sln`.)*
- **Python ML service:** `src/AgriForecast.ML`; its virtualenv is `src/AgriForecast.ML/.venv` — `agri-reviewer.md:51`, `agri-security.md:71`.
- **HARTI PDF cache:** `src/AgriForecast.ML/harti_cache/` — `agri-data-engineer.md:82`.
- **Market spelling variants / eras** are documented in `src/AgriForecast.ML/harti_multimarket_audit.md` — `agri-data-engineer.md:82`.
- **Analysis scripts** belong in `src/AgriForecast.ML/experiments/` (create if absent) — `agri-data-analyst.md:26`.

### 2.2 Frontend repo (separate)

- `Projects/Agri_Forecast/Project/UI/ForecastUI` — **its own git repo**, `DhananjayaSenadheera/ForecastUI`,
  separate from the backend monorepo — `agri-uiux.md:11`.
- Rule: frontend work **does not touch** `Project/Agri_Forecast/` — `agri-uiux.md:44`.

### 2.3 Database access

- SQL Server in Docker on `localhost,1434`, database `AgriForecast`; connection string in
  `AgriForecast.API/appsettings.json` — `agri-dotnet.md:32`.
- Query via `docker exec sql_server_container /opt/mssql-tools18/bin/sqlcmd ...`, `-C` to trust the cert — `agri-dotnet.md:32`.
- From Python: `cd src/AgriForecast.ML && set -a && source .env && set +a`, then `.venv/bin/python`.
  **Console-script shebangs are stale — always `python -m`.** Never print, copy or commit `.env` — `agri-data-analyst.md:17`.

### 2.4 ⚠️ Path hygiene note

Eight of the nine definitions hard-coded an absolute machine path for their shared memory directory
(see [§7](#7-memory--coordination-conventions)). The portable fleet's charter forbids hard-coded absolute paths in
durable agent files. Anything ported from these definitions should take the path from a project file, not
from a copied literal.

---

## 3. Architecture and contracts

### 3.1 The .NET ↔ Python boundary (the single most load-bearing contract)

Stated in `agri-dotnet.md:18`:

> The .NET API calls the Python FastAPI `POST /predict {cropId, plantDate}` and consumes
> `{harvestDate, predictedPrice, lowerBound, upperBound, confidence, activePredictor, modelVersion, explanation}`.

Rules attached to it:

- 🔴 **Normalise GUIDs to lowercase on the wire.** A real shipped bug: uppercase crop IDs silently missed the
  model's per-crop fallback dict and returned the wrong interval. `Guid.ToString()` is lowercase by default;
  keep it that way — `agri-dotnet.md:19`, `agri-backend-dev.md:42`.
- **The unit test did not catch it; the HTTP round-trip did.** Verify over HTTP, not just direct function
  calls — `agri-qa.md:42`, `agri-backend-dev.md:42`.
- **Compatibility shape of the .NET client:** `HarvestPredictionClient` is **case-insensitive + ignores
  unmapped fields** (proven: `topFactors` / `plantDate` are already unmapped). **Adding response fields is
  safe; renaming, removing or retyping `confidence` / `activePredictor` / `predictedPrice` / the bounds breaks
  it** — `agri-backend-dev.md:67`.
- **Pass the low-confidence / fallback flag straight through** to the client. Never upgrade a `Low`-confidence
  fallback into a confident-looking number — `agri-dotnet.md:20`.
- ML service base URL resolves from `appsettings`, with timeout + retry; if the ML service is down, return a
  structured error or a clearly-flagged fallback — `agri-dotnet.md:21`.
- **The browser never calls the Python service.** The .NET API is the frontend's only backend — `agri-uiux.md:13`.
- The .NET `ForecastController` is backed by Python `/predict` **and `/timeline`** internally — `agri-uiux.md:43`.

**Ownership split:** the Python `/predict` belongs to backend-dev; the .NET `ForecastController` + typed
`IHarvestPredictionClient` belong to the .NET owner; they meet at the HTTP contract and must not duplicate it —
`agri-backend-dev.md:40`, `agri-dotnet.md:8`, `agri-dotnet.md:15`.

**Frozen vocabulary:** `"Low"` / `"Medium"` / `"High"` confidence spelling is **frozen in the contract** —
never remap the strings, only translate the display label. There is also a `confidenceReason` field —
`agri-uiux.md:23`. *(Note: `confidenceReason` does not appear in the field list at `agri-dotnet.md:18`; see [§8](#8-open-contradictions).)*

### 3.2 Serving path (Python)

- Serve the **gated predictor** (model if promoted, else fallback) via `registry.load_promoted()`; **load once
  at startup** — `agri-backend-dev.md:41`.
- **Serving reads ONE precomputed `CropFeatureDaily` row** (`predict.py:26-34`). New feature families must be
  **offline columns** — never a per-request market fan-out — `agri-backend-dev.md:66`.
- **Any new served model kind needs BOTH** an entry in `_SERVABLE_ML_KINDS` (`predict.py:50`) **AND** an
  artifact-presence branch in `_ml_servable` (`predict.py:58-82`) — otherwise it silently falls back — `agri-backend-dev.md:68`.
- **SHAP is TreeExplainer-only** — Prophet/LSTM predictions would ship empty `topFactors`. That is a safe
  degrade to document, not a bug to "fix" — `agri-backend-dev.md:69`.
- `_build_X` was **duplicated** at `predict.py:85-99` vs `explain.py:82-92`; consolidated into a shared helper
  in P4 step 0, NaN-never-0 preserved — `agri-backend-dev.md:65`. *(Status disputed — see [§8](#8-open-contradictions).)*

### 3.3 Reference-data reads — the `load.py` house pattern

- **`load.py` is the house pattern for ALL reference-data reads**: direct SQL via `db.get_engine()`
  (SQLAlchemy), with try/except-empty-frame degradation for optional sources. `load_fx` and `load_policy_flags`
  are the templates. New calendars/reference tables get a `load_*()` there — **never an HTTP hop for static
  data, never a static-Python twin of a DB table** — `agri-backend-dev.md:49`.
- Calendars live in the DB and are read via that pattern (`load_festivals()` shaped like `load_policy_flags()`).
  `poya_days.py` is a **QA / gap-suppression tool, NOT a feature-data template** — `agri-ml-engineer.md:59`.
- `to_prophet_holidays()` exists at `load.py:191` and was **unused** as at 2026-07-04 — `agri-ml-engineer.md:79`.
- **When retiring hardcoded feature logic, DELETE it.** Never leave the old definition live in parallel with the
  new data-driven one — e.g. `_is_festival`'s Apr 12–15 window vs the calendar table's 13–14: two silently
  disagreeing definitions of the same feature is a shippable bug — `agri-backend-dev.md:50`.

### 3.4 FastAPI admin routes

- **All new admin routes register on the existing `admin_router`**, which inherits the fail-closed `X-API-Key`.
  A route registered on the bare `app` skips auth and regresses finding F-02 — `agri-backend-dev.md:51`,
  `agri-security.md:78`, `agri-reviewer.md:60`.
- Error-handling precedent to copy: `/admin/ingest-harti` at `serving/app.py:238-251` — generic `HTTPException`
  details, `_log.exception` server-side, import-failure → 503, **never interpolate `{exc}` into `detail`** —
  `agri-backend-dev.md:57`, `agri-backend-dev.md:59`.
- A global `@app.exception_handler(Exception)` returning a generic 500 was **approved as part of P3** because
  error hygiene was per-route try/except only — `agri-backend-dev.md:58`, `agri-security.md:83`.

### 3.5 .NET conventions (match these exactly)

- Namespace casing quirk: **`AgriForecast.Application.common`** — lowercase `c` — `agri-dotnet.md:24`.
- DI registration lives in `InfsDependencyInjection` — `agri-dotnet.md:24`.
- Migrations are deliberate: one per model change, review generated SQL, apply with `database update`;
  **nullable-by-default for new columns** so existing rows are safe; decimal columns get explicit precision —
  `agri-dotnet.md:25`.
- 🔴 **`AgriForecastDbContextModelSnapshot.cs` is a merge hazard.** Never scaffold a migration from a branch that
  lacks another branch's unmerged migrations. Branch **after** the open PR merges, or scaffold against the
  source branch and regenerate the snapshot (take main's, re-run `migrations add`) — **never hand-merge it** —
  `agri-dotnet.md:47`.
- **`PolicyFlag` is the template for point-in-time reference entities** the ML as-of-joins on — not `Market`
  (a CRUD dimension), not `EconomicIndicator` (a plain reading). Date-only columns
  (`HasColumnType("date")`, no hidden time component), seeded via `HasData` in a `Seed*()` DbContext method with
  **fixed GUIDs and fixed `CreatedAtUtc`** (a `UtcNow` in seed data churns every migrations diff).
  **`CreatedAtUtc` is record-keeping only — never a feature** — `agri-dotnet.md:45`.
- **No CQRS command / endpoint / ingestion service for yearly-static seed data** — that is overbuild. The
  deliverable in lieu of an endpoint is a documented update path in a comment block on the seeder — `agri-dotnet.md:46`.
- For open sets seeded as data, **string keys beat enums** (a new member is a seed row, not enum + migration +
  Python mirror) — but flag the deviation from the local enum-int convention as deliberate in an XML comment.
  **Per-occurrence rows beat recurrence-rule columns** — movable dates come free — `agri-dotnet.md:48`.
- **"Extends `<existing entity>`" in a spec means a SIBLING table, not EF inheritance** — `agri-dotnet.md:54`.

---

## 4. Data — sources, tables, measured facts

### 4.1 Where ingestion lives

- 🔴 **Ingestion already lives on the .NET side** — self-healing Dambulla price ingestion + Open-Meteo weather —
  **not Python**. The Python side's dataset work is the feature store `CropFeatureDaily` — `agri-data-engineer.md:39`.
- The `AgriForecast.Ingestion` worker: self-healing Dambulla price ingestion **auto-provisions a Crop per
  product**; Open-Meteo weather ingestion; idempotent and self-healing — `agri-dotnet.md:14`.
- Ingestion scrapes **HARTI PDFs, CBSL, and the Dambulla DEC REST API** daily — `agri-security.md:46`.

### 4.2 Key tables (as profiled 2026-07-04 — `agri-data-analyst.md:18`)

| Table | Notes |
|---|---|
| `MarketPrices` | training labels; **zero-price rows exist and are signal** |
| `PriceObservations` | Min/MaxPrice only — **Wholesale/Retail columns are all 0**; `AvgPrice = (Min+Max)/2` midpoint; **no zero-price rows** |
| `CropFeatureDaily` | the feature store, **~47.5k rows × 72 cols** |
| `MacroSeriesPoints` | CBSL macro vintage series |
| `NewsSentimentDaily` | news sentiment |
| `FestivalCalendarEntry` | festival calendar |
| `PolicyFlag` | point-in-time policy flags |

Also named across the set: `Crops`, `CropCategories`, `CropAgronomyProfiles`, `CommodityAliases`,
`EconomicIndicator`, `EconomicCenters`, `Markets`, `IngestionWatermark`.

🔴 **Missingness conventions DIFFER per table — never mix them per-row:**
- `MarketPrices`: zero-price rows are **kept as market-closed signal**; filter `MaxPrice > 0` at *feature* time,
  do not delete — `agri-data-engineer.md:40`, `agri-data-engineer.md:73`.
- HARTI / `PriceObservations`: **zero-price = missing, not signal** — the 196 known clustered zero rows are gaps
  — `agri-data-engineer.md:82`.

### 4.3 Markets

- 🔴 **Use `agriforecast_ml.canonical.get_feature_safe_market_ids()`** — NEVER a raw `AVG` over
  `PriceObservations`: Pettah/ECOMAP twins double-count, and **`MKT00000006` is a CBSL pseudo-market** —
  `agri-data-analyst.md:19`.
- **"National" = UNWEIGHTED mean over feature-safe markets**; NaN not 0 when fewer than 2 markets report —
  `agri-data-analyst.md:19`, `agri-ml-engineer.md:81`.
- `get_feature_safe_market_ids()` is at `canonical.py:347` and was **dead code until P4** — `agri-ml-engineer.md:81`.
- **Narahenpita is feature-safe but has 0 rows** — `agri-data-analyst.md:19`.
- **12 markets exist**, flagged by `IsEconomicCenter`; **Dambulla = `MKT00000001`** — `agri-uiux.md:43`.
- `EconomicCenters` deletion order: Eco rows **before** their ECOMAP twin Markets (Restrict FK) — `agri-reviewer.md:81`.

### 4.4 Crops and coverage

- **Only 4 of 11 model crops have `PriceObservations` coverage: Capsicum, Bitter Gourd, Ridge Gourd,
  Lady's Fingers.** The other 7 get NaN spread features by construction — `agri-ml-engineer.md:82`,
  `agri-data-analyst.md:19`.
- **Crop history is bimodal**: ~170–330 rows vs ~2,600–2,700 rows. Always segment thin vs rich crops; pooled
  means across a bimodal population mislead — `agri-data-analyst.md:20`, `agri-data-analyst.md:23`.
- **GUIDs are lowercase** — `agri-data-analyst.md:20`.
- `gap_report()` groups by **raw `ExternalCommodityName`** (e.g. "Luffa"), not resolved `Crop.Name` — translate
  before cross-referencing — `agri-data-engineer.md:72`.
- Feature builds on `PriceObservations` must filter **`IsUnitConfirmed = 1`** (canonical.py docstring contract;
  **537 held rows corpus-wide**) — `agri-data-engineer.md:74`.

### 4.5 Measured facts with dates (do not re-derive; re-verify if load-bearing)

| Measurement | Value | Source |
|---|---|---|
| `PriceObservations.ArrivalsKg` populated | **0 of 52,755** — a schema stub; arrivals live in HARTI's separate weekly bulletin, **never ingested** | `agri-data-engineer.md:70` (P4, 2026-07-04) |
| HARTI-vs-HARTI row agreement | identical, **corr 1.0** | `agri-data-engineer.md:71` |
| DEC-window Dambulla vs `MarketPrices` | only **12.6% exact match**, **~7.3% median diff** — never mix the two tables per-row for the same market/day | `agri-data-engineer.md:71` |
| HARTI PDF corpus | **2,977 PDFs, 2015-06-22 → 2026-07-01**, already cached in `harti_cache/`; re-parse with `--no-download`, **zero new scraping** | `agri-data-engineer.md:82` |
| `EconomicCenterId` backfill | **DAMBULLA_DEC 33,201 + HARTI 14,576 = 47,777 linked, 0 NULL** | `agri-qa.md:83` |
| `CropFeatureDaily` size | **~47.5k rows × 72 cols** | `agri-data-analyst.md:18` |
| Leakage-by-truncation result | features bit-identical, **max diff 0.00e+00** | `agri-qa.md:41` |
| pip-audit baseline pre-P4 | **CLEAN, 77 packages** | `agri-security.md:87` |
| Python dependency vulns (first audit) | **26 vulns across 11 packages** | `agri-security.md:68` |
| Training history starts | **2015-06-22** | `agri-data-engineer.md:48`, `agri-reviewer.md:52` |
| Data depth at Model A gating | **~13 months** | `agri-ml-engineer.md:42` |

### 4.6 Calendars and festivals (domain rules)

- 🔴 **Reference/calendar seeds MUST span the full training history (2015-06-22 →), never just "N years
  forward".** A forward-only seed silently zeroes the feature for **~95% of training rows** and CV still looks
  fine — described as "the worst class of bug". Always tie seed span to `MIN(training date)`.
  `poya_days.py` (2015–2030, confidence-tiered) is the correct precedent — `agri-data-engineer.md:48`.
- **Avurudu is solar** (Meena→Mesha ingress), a **two-day pair (Apr 13 + 14)** with the *nonagathe* between.
  Store the pair; anchor lead-up windows on the pair **START (Apr 13)**. Authoritative source: **Dept. of
  Government Printing annual holiday gazette**. Future years are **PROVISIONAL until gazetted**; an annual
  verification task exists — **ClickUp `86caj358h`, ~November yearly** — `agri-data-engineer.md:49`.
- **Eid dates need ACJU moon-sighting verification** — Sri Lanka's observed date can differ **±1 day** from any
  generic Islamic-calendar computation — `agri-data-engineer.md:50`.
- **Count independent EVENTS, not rows.** Pooling crops multiplies rows but every crop sees the same April-2019
  Avurudu — correlated observations, not samples. **~10 events per festival** ⇒ a merged "any-event" feature
  beats per-event features. With 1–2 events per CV fold, event-feature lift is **statistically unverifiable** —
  say so in the training report ("added on domain prior + leakage-safety, not CV-proven") — `agri-ml-engineer.md:56`.
- **Fix event windows at a domain prior; never tune them on full-dataset prices** — the window boundary itself
  leaks — `agri-ml-engineer.md:57`.
- ⚠️ **Naming collision:** the string **"R1.1 P2"** in code/tests means the **Thambuttegama/Keppetipola parser
  extension**, NOT the festival phase. Check for label collisions before reusing phase names in commits/branches
  — `agri-data-engineer.md:51`.

### 4.7 CBSL macro / vintage data

**Pre-flight results, web-verified 2026-07-04** (`agri-data-engineer.md:58`) — these **supersede** the
2026-07-03 preview at `agri-data-engineer.md:52`:

- The CBSL corpus is **PDF-heavy, not Excel**.
- Daily Price Report: **PDF-only, daily**, `price_report_YYYYMMDD_e.pdf`, roughly **D-1**.
- **CCPI**: monthly PDF press releases, published **~last business day of the REFERENCE month**, publish dates
  on the listing page.
- **NCPI**: published by **DCS**, **~21st of the FOLLOWING month**.
- **Correction to the preview:** monthly first prints are effectively **FINAL** — the real revision risk is
  **base-year rebasing (2013=100 → 2021=100)**, not month-to-month revision.
- **Vintage policy (user-decided):** first-print + `KnowledgeDate` = the real publication date; **base year is
  part of the series key**; **no silent splicing**.

Vintage handling rules:

- 🔴 **Vintage rows carry TWO dates; join on `PublishedAt`, NEVER `ReferenceDate`.** `ReferenceDate` = the period
  described; `PublishedAt` = when the world could know it. Backward as-of on `PublishedAt` is the leakage gate;
  `ReferenceDate` is **audit-only and must be dropped before the model frame**. Both are plausible-looking
  `DateTime` keys — a copy-paste from the single-date `_attach_fx` can silently pick the wrong one —
  `agri-ml-engineer.md:67`, `agri-reviewer.md:59`.
- **`PublishedAt = ReferenceDate` backfill is ANTI-conservative** (it asserts a monthly index was knowable on its
  reference date when it publishes weeks later → lookahead). Correct default =
  **`ReferenceDate + per-series publication-lag prior`**; use the real release date when scrapeable; **flag
  imputed vintages** — `agri-ml-engineer.md:68`.
- **`PublishedAt` resolution order:** PDF `/CreationDate` (authoritative — `harti/loader.py
  _parse_pdf_creation_date` is the precedent) → listing-page date (fallback) → conservative-LATE imputation
  (`ReferenceDate + per-series lag prior`). Every imputed vintage flagged (`IsPublishedAtImputed`, following the
  sticky-down `IsUnitConfirmed` precedent). **When in doubt pick LATER** — over-conservative only delays a join,
  never leaks — `agri-data-engineer.md:62`.
- **Prefer the publisher's official YoY series over self-computed YoY from levels** — base-invariant across
  rebasing with no silent splice. If deriving from levels: base year is part of the `SeriesCode` key, and **YoY
  spanning two bases = NaN, never spliced**. LKR-level features (imports) drift with FX/inflation — use YoY
  %-change — `agri-ml-engineer.md:69`.
- **Staleness cap** on carried-forward monthly values: P3 decision — newest vintage older than **~60 days** at
  `ObservationDate` → **NaN**. FX/sentiment carry forward uncapped, which is fine for ~daily series and wrong
  for monthly — `agri-ml-engineer.md:70`.
- 🔴 **Macro NaN means "not knowable", never 0** — a deliberate contrast with `_attach_policy`'s
  0-means-no-active-policy. **Do not copy the policy fill** — `agri-ml-engineer.md:71`.
- **Vintage entity shape:** `PublishedAt` is date-only (`HasColumnType("date")`) and part of the unique key;
  `ExistsAsync` must key on the **FULL unique triple `(SeriesCode, ReferenceDate, PublishedAt)`** or a revised
  print is wrongly skipped as already-present. Guard `ReferenceDate <= PublishedAt` in the constructor —
  `agri-dotnet.md:56`.
- **One table owns a series.** `EconomicIndicator` owns daily `USD_LKR` (written by `EconomicIngestionService`,
  read on the Python side as `FxUsdLkr`). Putting `USD_LKR` in a second table is a **dual-write collision** —
  adjudicate ownership before writing either — `agri-dotnet.md:55`, `agri-reviewer.md:61`.
- `EconomicIndicator` is a live mapped table with a `(Date, IndicatorCode)` unique key and a `Value<=0`
  constructor guard — TPH/TPT off it would churn a shipped table, and its constraints are wrong for vintage data
  (YoY can be ≤ 0) — `agri-dotnet.md:54`.

**Agency ownership (get this right before bundling an indicator into a source's ingestor):**
- **`DIESEL_PRICE_LKR` is CPC / Ministry of Energy, not CBSL.**
- **NCPI is DCS, not CBSL.**
- A "CBSL" service scraping prose mentions of another agency's number is the guessing-parser anti-pattern —
  `agri-data-engineer.md:60`.

**Scheduling:** check cron timing against **actual publication calendars** — the specced "~15th monthly" would
have missed NCPI (~21st of the next month) **every month**. "No new bulletin since watermark" = **SUCCESS with
zero rows, never an error**. Use **per-series watermark rows** so one late series never fails another —
`agri-data-engineer.md:61`, `agri-dotnet.md:59`.

**Scope decisions:** NCPI was **CUT** from scope rather than allowlist a second host — keep the SSRF allowlist
**single-apex** (`cbsl.gov.lk` only; `statistics.gov.lk` stays OFF). The **eResearch portal is manual one-time
backfill only** (ASP.NET POST-backs) — `agri-data-engineer.md:63`, `agri-security.md:82`.

**Reuse, don't rewrite:** `harti/downloader.py` `scrape_pdf_links` (permissive scrape + drop counters) and
`_download_capped` are the CBSL templates — `agri-data-engineer.md:64`.

**Method warning:** *listing-page existence/cadence probes are NOT extractability probes.* Before designing any
parser, download and text-extract 2–3 real artifacts — this is called "the HARTI lesson". **Existence ≠
parseability** — `agri-data-engineer.md:59`.

### 4.8 Alias / parser lockstep rule

🔴 **Aliases in LOCKSTEP (fail-closed).** Extending parser targets (`_TARGET_CROPS`, market aliases) **REQUIRES**
extending the DB `CommodityAliases` in the **same change** — the resolver is fail-closed: unmapped external names
→ `CropId NULL` + WARN, and those rows **silently drop out of features**. Acceptance for any parser-widening
change: **zero unmapped WARNs on re-parse**, then run `heal_price_observation_crops()` —
`agri-data-engineer.md:80`, `agri-qa.md:82`, `agri-reviewer.md:77`.

### 4.9 Backfill rule

🔴 **Backfills of historical rows are PER-SOURCE, never blanket** — one `UPDATE` per `Source` value with
before/after row counts verified and reported, **even when the target value happens to be the same across
sources** (e.g. `MarketPrices.EconomicCenterId`: DAMBULLA_DEC and HARTI updated separately). A blanket-count
check can pass while one source was missed — `agri-data-engineer.md:81`, `agri-dotnet.md:73`, `agri-qa.md:83`,
`agri-reviewer.md:76`.

---

## 5. Model, features and the promotion gate

### 5.1 What exists

- **Model A only: a pooled XGBoost** (crop as a categorical feature). Prophet/LSTM are **later phases**, and as
  at 2026-07-03 **no Prophet/LSTM code existed** — `agri-ml-engineer.md:40`, `agri-ml-engineer.md:61`.
- **Registry:** file-based — `models/<version>/model.pkl` + `metadata.json` + `promoted.json` — `agri-ml-engineer.md:40`.
- **Status (2026-06-23):** Model A is trained and gated. It **beats carry-forward but NOT the per-crop mean**, so
  the promotion gate **correctly serves the crop-mean fallback** (per-crop P10/P90). This is expected at ~13
  months of data and **auto-promotes the ML model once it earns it** — `agri-ml-engineer.md:42` (repeated in
  `agri-backend-dev.md:37`, `agri-data-engineer.md:36`, `agri-qa.md:38`, `agri-reviewer.md:37`).

### 5.2 The promotion gate

- 🔴 **Gate against the BEST baseline** — the minimum of carry-forward and crop-mean — **not the weakest.**
  A model worse than a per-crop average is not shippable, "and that is currently the case" —
  `agri-ml-engineer.md:45`, `agri-qa.md:43`, `agri-reviewer.md:40` (reviewer treats this as **blocking**).
- **Ship P10 / P50 / P90 intervals, not point estimates** — `agri-ml-engineer.md:47`.
- **Thin data → one pooled model** with crop as a categorical feature beats per-crop models — `agri-ml-engineer.md:47`.
- **Per-horizon gating must be ADDITIVE to `metadata["cv"]`** (nested `by_horizon` blocks); the flat schema and
  the existing gate-honesty tests must stay green; per-horizon baselines are recomputed per bucket; **refuse to
  claim a result for a bucket that cannot meet the min-rows fold guard** — `agri-qa.md:73`.

### 5.3 The label

🔴 **`GrowthPeriodDays` DEFINES the training label (`price.shift(-GrowthPeriodDays)`) AND the serving horizon.**

- Its source of truth **moved to `CropAgronomyProfiles`** (1:1 with Crop) — `load.py` / `features.py` /
  `serving/predict.py` read the profile table, **not `Crops` columns** — `agri-ml-engineer.md:90`, `agri-backend-dev.md:77`.
- **Changing a crop's VALUE silently changes its label** → **owner sign-off required per value**. Retraining
  happens **ONCE at plan Step 7**, never per-step; value-preserving schema moves do **not** retrain —
  `agri-ml-engineer.md:90`, `agri-dotnet.md:74`. Conversely, a retrain triggered by a value-preserving schema
  move is **also wrong** — `agri-reviewer.md:79`.

### 5.4 Feature engineering rules specific to this project

- 🔴 **Anchor features to the PREDICTION-TARGET date, not the observation date.** The label is a *harvest-time*
  price. For long horizons, "state of the world now" features (e.g. `days_to_next_festival` at observation time)
  can be irrelevant or anti-correlated; the load-bearing variant is anchored on **`HarvestDate`**
  (`features.py` ~L105), which is deterministic and legal. Always ask: does this feature describe the world at
  **label time** or **observation time**? — `agri-ml-engineer.md:55`, `agri-reviewer.md:53`.
- **Weather features are point-in-time = last COMPLETE month (M-1)**, never the in-progress month —
  `agri-ml-engineer.md:46`, `agri-data-engineer.md:41`; the reviewer treats M-1 weather as **blocking** — `agri-reviewer.md:40`.
- **Trees are invariant to monotone encodings** — a **clipped linear countdown (cap ~30–45 days)** is the right
  XGBoost encoding; don't waste columns on buckets/decay. Decay/holiday encodings belong to Prophet/linear
  models — and Prophet has **native `holidays` support** (name / ds / lower / upper windows), so design calendar
  loaders whose output reshapes into a Prophet holidays frame — `agri-ml-engineer.md:58`.
- **New feature columns auto-enter training** via `dataset.feature_columns` auto-include, and **`contract_hash`
  guards train/serve skew** — but **every new column needs an `explain._LABELS` entry** or SHAP shows raw
  column names to farmers — `agri-ml-engineer.md:60`.
- **Latent debt:** policy / FX / sentiment columns are **missing from `explain._LABELS`** — backfill labels
  whenever touching `_LABELS` — `agri-ml-engineer.md:73`; also a reviewer should-fix — `agri-reviewer.md:62`.
- 🔴 **Check for already-shipped features before building.** P3's specced budget flags
  (`PolicyImportBanActive` etc.) **already existed verbatim** in `_attach_policy` (`features.py:371-379`) —
  `agri-ml-engineer.md:72`.
- **National macro columns are identical across crops on a date → no cross-sectional signal; expected CV lift
  ≈ 0.** State that in the training report; **don't promote on fold noise** — `agri-ml-engineer.md:72`.
- **Shrinkage / category priors must be computed train-only per fold**, never on full data — `agri-reviewer.md:69`.

### 5.5 Metrics

- `evaluate.py` had **MAE / RMSE / MAPE only**; **directional accuracy was added in P4 step 0** — report it
  alongside MAE in CV output — `agri-ml-engineer.md:83`.
- Directional accuracy matters because the product is a go/no-go call — `agri-ml-engineer.md:29`.

### 5.6 Environment gotchas for training

- macOS: **`xgboost` needs `brew install libomp`**.
- **Python 3.9** → add `from __future__ import annotations` for `X | None` hints.
— both `agri-ml-engineer.md:48`.

---

## 6. What exists vs what is roadmap

### 6.1 Built and shipped (as at the note dates)

| Thing | Evidence |
|---|---|
| .NET 9 Clean-Architecture API, 4 layers, CQRS/MediatR, EF Core 9 | `agri-dotnet.md:11-13` |
| `AgriForecast.Ingestion` worker: Dambulla prices (self-healing, auto-provisions Crop) + Open-Meteo weather | `agri-dotnet.md:14`, `agri-data-engineer.md:39` |
| Python FastAPI ML service with `/predict`, `/timeline`, `/admin/*` | `agri-uiux.md:43`, `agri-backend-dev.md:57` |
| `ForecastController` + typed `IHarvestPredictionClient` + recommendation matrix (🟢🟡🔴) | `agri-dotnet.md:15` |
| Model A (pooled XGBoost), trained + gated, currently serving the crop-mean fallback | `agri-ml-engineer.md:42` |
| File model registry with signed payload + `_verify_integrity` | `agri-ml-engineer.md:40`, `agri-security.md:88` |
| Feature store `CropFeatureDaily` (~47.5k × 72) | `agri-data-analyst.md:18` |
| HARTI PDF corpus cached, 2,977 PDFs 2015-06-22 → 2026-07-01 | `agri-data-engineer.md:82` |
| SHAP explanations (TreeExplainer) with `explain._LABELS` | `agri-backend-dev.md:69`, `agri-ml-engineer.md:60` |
| P1 step-7 security controls, commit `e07b4d4` (2026-07-03): `netguard.py` SSRF guard, parse timeout, 25MB download cap, `admin_router` fail-closed `X-API-Key` | `agri-security.md:74` |
| Leakage-by-truncation test suite (`TestLeakageByTruncation`) | `agri-qa.md:41`, `agri-qa.md:54` |
| `PolicyFlag`, `FestivalCalendarEntry`, `MacroSeriesPoints`, `NewsSentimentDaily` tables | `agri-data-analyst.md:18` |

### 6.2 Explicitly NOT built (as at those dates)

| Thing | Status | Evidence |
|---|---|---|
| **Prophet** | roadmap; P4 = a thin **gated R&D spike** only (1–2 crops, holidays-only, MAP not MCMC, must beat seasonal-naive AND Model A or it does not ship) | `agri-ml-engineer.md:61`, `agri-ml-engineer.md:79` |
| **LSTM** | roadmap; **deferred out of P4** | `agri-ml-engineer.md:79` |
| **MLflow** | "only a future option" | `agri-ml-engineer.md:40` |
| **torch / tensorflow** | NOT added in P4 — 2GB, and `torch.load` is default-RCE | `agri-security.md:87` |
| **`openpyxl`** | not installed as at 2026-07-04 | `agri-security.md:81` |
| **camelot / tabula** | absent — "keep it that way", pure-Python parsers only | `agri-security.md:81` |
| **HARTI weekly arrivals bulletin** | never ingested; `ArrivalsKg` is 0/52,755 | `agri-data-engineer.md:70` |
| **`PredictionLog` entity** | did not exist → no PII-in-logs problem at that time | `agri-security.md:60` |
| **Role-based auth in the .NET API** | none — every controller is bare `[Authorize]` | `agri-dotnet.md:57`, `agri-security.md:84` |
| **`marketId` on `/predict` + a market-keyed store** | **P5** — "do not build early" | `agri-dotnet.md:65` |
| **Market-as-label reshape** | P5 | `agri-ml-engineer.md:79` |
| **Learned festival uplift** | deferred to the Prophet path | `agri-ml-engineer.md:79` |
| **Wall-clock timeouts / subprocess isolation for training** | P5 | `agri-security.md:89` |
| **Dependency CVE scanning as a standing baseline** | had none on record at first audit | `agri-security.md:59` |

### 6.3 Phase map as these files describe it

- **P1** — ingestion + security controls (step 7 shipped 2026-07-03, commit `e07b4d4`).
- **P2** — festival/calendar reference data (2026-07-03 analysis).
- **P3** — CBSL macro vintage series (2026-07-04 analysis).
- **P4** — cross-market ensemble features + a gated Prophet spike (2026-07-04 analysis).
- **P5** — market-keyed predictions, HMAC/side-car artifacts, training isolation (named only as future).
- **P6** — leakage/vintage test suite (`agri-qa.md:54`).
- **R2** — data foundation, **owner-approved and binding** spec pins dated 2026-07-05.
- **Phase 4 (Farmer Frontend)** — ClickUp list `901524201150` (see §6.4).
- **Step 8.2 shadow window** — backend hold until ~2026-07-16 (`agri-uiux.md:44`).

### 6.4 Frontend state (2026-07-09 — `agri-uiux.md:41-45`)

- **ForecastUI is a stale scaffold**: one screen (crop-create form), **last commit 2026-02-03**. Treat it as a
  starting point, **not an architecture**.
- 🔴 **The API has moved under it since February**: crops re-coded to `VEG######` / `FRT######`; **categories are
  mandatory at registration** (`CropCategories` table); 12 markets exist (`IsEconomicCenter`, Dambulla =
  `MKT00000001`); the forecast surface is the .NET `ForecastController`. **Audit `api.js` + the create-crop
  payload against the live API before building anything on top.**
- **Backend hold window until ~2026-07-16** (Step 8.2 shadow window); frontend work is isolated in its own repo
  and must not modify `Project/Agri_Forecast/`.
- **ClickUp scope — Phase 4 Farmer Frontend, list `901524201150`:**
  React mobile-first setup `86cacw5wq` · crop picker + plant-date selector `86cacw5wy` · price forecast chart
  with harvest marker `86cacw5x5` · recommendation card `86cacw5xg` · factor breakdown panel `86cacw5xq` ·
  Sinhala/Tamil i18n `86cacw5y2` · HCI/usability review `86cacw5yf`.

### 6.5 Product/domain constraints on the UI (project-specific, not generic UX)

- **Users are Sri Lankan farmers deciding what to plant and when to sell.** Mobile-first, mid-range Android,
  **outdoors in sunlight**, low digital literacy, **rural bandwidth**, **trilingual Sinhala / Tamil / English**
  — `agri-uiux.md:16-20`.
- 🔴 **Honest uncertainty is the product principle.** The backend's defining feature is an honest promotion gate;
  many crops serve a fallback with `confidence: "Low"` and an explicit `confidenceReason`. The UI must surface
  that honestly and **never dress a fallback up as a precise prediction**. **Hiding uncertainty is a product
  bug, not a styling choice** — `agri-uiux.md:23`.
- Forecast ranges are labelled as ranges (**P10–P90 band**), never a fake single number; LKR currency and
  locale-correct dates — `agri-uiux.md:35`.
- **The number IS the product — never chart-only**; charts get text alternatives — `agri-uiux.md:29`.

---

## 7. Memory & coordination conventions (the old fleet's wiring)

Eight of the nine definitions (**all except `agri-data-analyst.md`**) ended with an identical block titled
**"Ecosystem coordination protocol (AgriForecast — apply every task)"** — e.g. `agri-ml-engineer.md:97-123`,
`agri-backend-dev.md:81-107`, `agri-dotnet.md:79-105`, `agri-uiux.md:47-73`.

Its durable content:

- **The main thread is the hub.** Agents never spawn or message other agents; coordination is asynchronous via
  shared files.
- **Shared memory directory** (`<MEM>`), hard-coded in all eight as:
  `/Users/dhananjayasenadheera/.claude/projects/-Users-dhananjayasenadheera-Projects-Agri-Forecast-Project-Agri-Forecast/memory`
  *(verified to exist 2026-08-25, containing `MEMORY.md`, `DECISIONS.md`, `CONTRACTS.md`,
  `beat-crop-mean-is-data-wall.md`, `economic-factor-not-in-predictions.md`, `local-run-setup.md`,
  `promote-v7-residual-plan.md`, `sync-clickup-on-task-completion.md`, `use-agents-for-all-dev-tasks.md`).*
- **Three shared files, by purpose:**
  - `MEMORY.md` — index of long-term lessons; **read first, always**; open only the `[[linked]]` files relevant
    to the task.
  - `DECISIONS.md` — **append-only** design decisions + outcomes (the "why we chose X").
  - `CONTRACTS.md` — API shapes, feature-store schema, model-registry layout, ports/integration.
- **Before implementing:** grep `DECISIONS.md` + `CONTRACTS.md` for the area being touched; **reuse** what is
  recorded; if diverging from a recorded decision or contract, **say so explicitly and why**; state a one-line
  plan naming the contracts/decisions relied on.
- **After implementing:** end the final message with a **WRITE-BACK block** — the agent does **not** need write
  access; the hub persists it. Fields:
  ```
  ### WRITE-BACK
  DECISION: <what was decided + why + measured outcome>
  CONTRACT: <new/changed interface, schema, route, or registry shape>
  LESSON:   <gotcha / failure / non-obvious constraint worth remembering>
  REUSE:    <existing code or solution you reused, or that peers should reuse>
  CLICKUP:  <ClickUp task (name/id) this work maps to + whether it is now FULLY done (merged/verified); the hub syncs the board at the final-completion gate>
  ```
- **Token economy (mandatory):** read the index before full files; return **summaries, not transcripts**; never
  re-run analysis already captured in `DECISIONS.md` / `MEMORY.md` — cite it instead.
- **ClickUp is the board of record**, synced by the hub at the final-completion gate (the `CLICKUP:` field).
  Known ClickUp identifiers preserved here: list `901524201150` (Phase 4 Farmer Frontend) and tasks
  `86cacw5wq`, `86cacw5wy`, `86cacw5x5`, `86cacw5xg`, `86cacw5xq`, `86cacw5y2`, `86cacw5yf`,
  `86cahefby` (P3 CBSL — carries 8 acceptance criteria in a 2026-07-03 analysis comment, plus comments
  `90150239063093` / `90150239067435`), `86caj358h` (annual Avurudu gazette verification, ~November).
- **Agent ground rules** (`agri-uiux.md:75`): repo path pinned in the definition; **STOP-and-report on
  unreadable files**; user-facing analysis/reports go back to the hub for visual presentation. The analyst had
  the same stop rule — "If a file/table you were told about is missing or unreadable, STOP and report — never
  reconstruct or fabricate" (`agri-data-analyst.md:33`).

⚠️ **Gap:** `agri-data-analyst.md` has **no** coordination block and no `<MEM>` pointer (the file ends at line 33).
Whether that was deliberate or an omission is not recorded anywhere in these files.

---

## 8. Open contradictions

Recorded, not resolved. Each shows both readings and which is newer.

### C-1 🔴 MLflow + 3-model ensemble vs file registry + Model A only
Full detail in [§1.2](#12--the-contradiction-inside-these-files--recorded-not-resolved).
**Newer/corrective:** file registry, Model A only (2026-06-23, grep-confirmed 2026-07-03).
**Still uncorrected in place:** the `description:` frontmatter of `agri-ml-engineer.md:3` and
`agri-backend-dev.md:3`, plus `agri-ml-engineer.md:11-17,25`, `agri-backend-dev.md:12,20`,
`agri-reviewer.md:18,20`.

### C-2 CropCode scheme — `CROP######`/`DMB######` vs `VEG######`/`FRT######`
- **Reading A** (`agri-dotnet.md:24`, undated): "the code-generation scheme (`CROP######` from DefaultSetting for
  manual crops; `DMB######` for auto-ingested)".
- **Reading B** (`agri-dotnet.md:75`, R2 spec pins **2026-07-05**): "**CropCode is display-only and assign-once
  immutable** (`VEG######`/`FRT######` — prefix reflects category at registration, never re-issued, never
  re-coded on category change). ML keys on lowercase GUID `CropId`; no FK/index/join may use CropCode."
- **Reading B corroborated** (`agri-uiux.md:43`, **2026-07-09**): "crops re-coded to `VEG######`/`FRT######`".
- **Newer/corrective: B.** A is the pre-recode scheme. Not established: whether `DMB######` still exists for
  auto-ingested rows alongside VEG/FRT, or was fully replaced — no source says.

### C-3 CBSL corpus format and revision risk
- **Reading A** (`agri-data-engineer.md:52`, preview **2026-07-03**): probe "per-series Excel/CSV availability
  (Excel ≫ lower risk than PDF extraction)"; "CCPI/NCPI are **ROUTINELY REVISED** after publication".
- **Reading B** (`agri-data-engineer.md:56-58`, pre-flight **2026-07-04**, explicitly headed "supersedes the
  preview above"): corpus is "**PDF-heavy, not Excel**"; "monthly first prints are effectively **FINAL** —
  base-year rebasing is the real revision risk, not month-to-month revision".
- **Corroborating B** (`agri-security.md:81`, 2026-07-04): "the live CBSL path is **PDF-only**; Excel/openpyxl
  appears ONLY on the manual eResearch backfill path".
- **Newer/corrective: B**, and it says so itself.

### C-4 Does `TestLeakageByTruncation` auto-cover new feature columns?
- **Reading A** (`agri-qa.md:54`, **2026-07-03**): "Existing `TestLeakageByTruncation` **auto-covers new
  columns** (iterates all non-excluded columns)".
- **Reading B** (`agri-qa.md:71`, **2026-07-04**, explicitly flagged "(Corrects the 2026-07-03 'auto-covers'
  note.)"): "`TestLeakageByTruncation` calls `build_all` with only **3 positional args** — it does **NOT**
  auto-cover columns attached via optional kwargs (fx/sentiment/policy/macro/festival/market). New attach-path
  columns need their own truncation-test instantiations passing the new frames explicitly."
- **Newer/corrective: B.** This one matters: believing A would leave whole feature families untested for leakage.

### C-5 Is the `_build_X` duplication fixed?
- **Reading A** (`agri-qa.md:55`, found **2026-07-03**, softened **2026-07-04**): `serving/predict.py` and
  `serving/explain.py` carry duplicated `_build_X` logic — latent train/explain skew; a dedup+parity-test task
  was spun off; "**until fixed**, any feature-column change must be checked in BOTH files". Softening: both build
  dynamically off `payload["feature_cols"]`, so new columns need no edit in either — the drift risk is
  behavioural handling, not missing columns; a parity test is still cheap insurance.
- **Reading B** (`agri-backend-dev.md:65`, P4): "`_build_X` **was** duplicated (predict.py:85-99 vs
  explain.py:82-92) — **consolidated into a shared helper in P4 step 0**; keep it single-sourced, NaN-never-0
  preserved."
- **Unresolved:** B claims the fix shipped in P4 step 0; A (same P4 round) still speaks of it as open-but-softened.
  Both are dated 2026-07-04. **Verify in code before relying on either.**

### C-6 Security posture — a documented self-correction
- **Earlier belief:** "no secrets / `appsettings.json` not git-tracked".
- **Corrected 2026-07-01** (`agri-security.md:49`), verbatim: "the earlier ... belief is **FALSE**. First audit
  confirmed a hardcoded SQL Server `Sa` password in TWO tracked files (`AgriForecast.API/appsettings.json:10`,
  `AgriForecast.Ingestion/appsettings.json:3`) **and in git history** (commit `a72805c`). All four
  `appsettings*.json` are git-tracked."
- **Newer/corrective:** the correction. See [§9.1](#91-the-open-critical) — status of the remediation is **not
  recorded** in these files.

### C-7 `/predict` response field list is inconsistent between sources
- `agri-dotnet.md:18` lists: `harvestDate, predictedPrice, lowerBound, upperBound, confidence, activePredictor,
  modelVersion, explanation`.
- `agri-uiux.md:23` additionally names **`confidenceReason`**.
- `agri-backend-dev.md:67` additionally names **`topFactors`** and **`plantDate`** as fields the .NET client
  leaves unmapped.
- **Not a conflict in values — a difference in completeness.** The `agri-dotnet.md:18` list is not exhaustive.
  Treat the real contract as authoritative; do not treat that list as a closed set.

### C-8 Zero-price rows — looks like a contradiction, is actually a per-table rule
- `MarketPrices`: **keep zero-price rows, they are market-closed signal** — `agri-data-engineer.md:40`.
- HARTI / `PriceObservations`: **zero-price = missing, not signal** (196 known clustered zero rows are gaps) —
  `agri-data-engineer.md:82`; and `PriceObservations` has no zero-price rows at all — `agri-data-analyst.md:18`.
- **Both are correct simultaneously.** Recorded here because the two statements read as a contradiction out of
  context and have a high cost if conflated — the files themselves warn "missingness conventions **DIFFER** per
  table, never mix them per-row".

### C-9 Which side owns ingestion
- The `agri-backend-dev` role text implies the Python service owns serving and model loading; the
  `agri-data-engineer` role text implies Python-side ingestion pipelines.
- **Corrected** (`agri-data-engineer.md:39`): "Ingestion already lives on the **.NET** side ... **not Python**."
- **Newer/corrective:** ingestion is .NET; the Python side owns the feature store. Note the *triggers* nuance:
  ingest triggers wire into the .NET Worker or the Python `admin_router` — **never** a bare-`[Authorize]` .NET
  controller (`agri-dotnet.md:57`).

---

## 9. Security findings carried forward

> ⚠️ These are audit findings dated **2026-07-01 to 2026-07-04**. **Whether any was remediated is NOT recorded in
> these files.** Re-verify each before treating it as open or closed.

### 9.1 The open Critical

**F-01 (Critical)** — hardcoded SQL Server `Sa` password in tracked config **and git history**:
`AgriForecast.API/appsettings.json:10`, `AgriForecast.Ingestion/appsettings.json:3`, git commit **`a72805c`**.
All four `appsettings*.json` are git-tracked. Prescribed remedy: **rotate → env-var/user-secrets → untrack →
history purge** — `agri-security.md:49`, `agri-security.md:63`.

### 9.2 First audit verdict, 2026-07-01 — 2 Critical, 4 High, 5 Medium, 3 Low (`agri-security.md:62-69`)

| ID | Sev | Finding | Location |
|---|---|---|---|
| F-01 | Critical | Hardcoded `Sa` password in tracked config + git history | `AgriForecast.API/appsettings.json:10`, `AgriForecast.Ingestion/appsettings.json:3`, commit `a72805c` |
| F-02 | Critical | FastAPI ML service has **NO auth**; `/admin/ingest-news` open | `serving/app.py:87`; explicit "No auth in this MVP" at `app.py:3` |
| F-03 | High | `model.pkl` pickle load via `joblib.load` — not remote-triggerable then (version from local `promoted.json`), but **any write to `models/` = RCE at reload**; path built from `promoted.json` version with **no `..` sanitisation** | `registry/registry.py:44` → `predict.py:23`; `registry.py:42-44` |
| F-04 | High | FastAPI returns raw `{exc}` in `detail` | `app.py:109/118/129` |
| F-05 | High | AutoMapper 12.0.1 CVE **GHSA-rvv3-g6hj-g44x** | .NET deps |
| F-06 | High | 26 Python vulns across 11 packages; on the untrusted-PDF/request path: **pdfminer-six→20251230, pillow→12.2.0, starlette, requests→2.33.0, urllib3→2.7.0** | Python deps |
| F-07..F-14 | Med/Low | CORS `AllowAnyOrigin` (`Program.cs:71-73/90`); **no rate limiting (auth included)**; Swagger `MapOpenApi()` ungated (`Program.cs:91`); no PDF size/bomb cap (`downloader.py:176-182`); `TrustServerCertificate` (dev-ok); dev JWT placeholder key; `AllowedHosts *`; scrape URLs in logs (log-only, ok) | as listed |

**Verified NOT an issue** (`agri-security.md:70`): XXE (pdfplumber, no XML parser); SSRF (host allow-lists);
cache path-traversal (filename derived from parsed date); .NET error leakage (generic `ProblemDetails`).

**Verified good** (`agri-security.md:50`): EF Core is all LINQ, **no raw SQL**; Python DB access fully
parameterised including untrusted news title/url; .NET `GlobalExceptionMiddleware` returns generic
`ProblemDetails`; ASP.NET Identity PBKDF2 hashing. Also: **no shuffled/K-fold splits and no
`fit_transform`-before-split** anywhere (`agri-security.md:51`).

**Tooling note:** `pip-audit` was installed into `src/AgriForecast.ML/.venv` to run the scan — "remove if
unwanted" (`agri-security.md:71`).

### 9.3 Controls shipped 2026-07-03, commit `e07b4d4` — copy these, don't hand-roll (`agri-security.md:74`)

- **`netguard.py`** — SSRF guard: host allowlist `_DEFAULT_ALLOWED_HOSTS`, **resolve-then-check private-IP
  block**, `guarded_get` with **redirects off + per-hop re-validation**.
- **`parser.py`** — wall-clock parse timeout (`ThreadPoolExecutor` + `future.result(timeout)`).
- **`downloader.py`** — streamed **25 MB** size cap.
- **`app.py`** — `admin_router` fail-closed **`X-API-Key`** (constant-time compare; unset key → 500).

### 9.4 Live security gotchas

- 🔴 **Allowlist foot-gun:** `AGRI_INGEST_ALLOWED_HOSTS` env **REPLACES** the built-in defaults — it does **not**
  extend them. Permanent new hosts (e.g. `cbsl.gov.lk`) go into **`_DEFAULT_ALLOWED_HOSTS` IN CODE**; the env var
  is an override-only escape hatch. The effective allowlist is INFO-logged at first use — `agri-security.md:75`.
- 🔴 **Choke-point bypass is the #1 regression risk:** `guarded_get` / `_download_capped` / the parse timeout are
  **per-call-site, not global**. Grep any new fetcher for bare `requests.get` / `pdfplumber.open` —
  `agri-security.md:82`, `agri-reviewer.md:60`.
- **Bare-`app` route registration** = an F-02 regression — `agri-security.md:78`.
- **Keep parse timeouts enabled** (config `0` disables them) — `agri-security.md:78`.
- **Accepted residual risks, documented in code:** DNS-rebind TOCTOU, feedparser internal redirects, abandoned
  timeout worker threads — `agri-security.md:78`.
- **Artifact integrity rule:** everything goes inside the **ONE signed `model.pkl` dict** (Prophet via a
  `model_to_json` string). Any side-car requires a **sha256 in `metadata.json` + a `_verify_integrity`
  extension** — an unsigned side-car load path is a P5 HMAC hole — `agri-security.md:88`, `agri-ml-engineer.md:84`.
- **Stan compilation must never run on the serving/import path** (the JSON round-trip avoids recompile; the
  payload loads at `predict.py` import) — `agri-security.md:90`.
- **P4 dependency policy (owner-approved):** `prophet>=1.1.6,<1.2` + `cmdstanpy>=1.2,<2` — floor **and** ceiling
  for heavy libs; the rest of `requirements.txt` stays **floors-only**. Re-audit after any install —
  `agri-security.md:87`.
- **If .NET ever fetches CBSL:** the registered CBSL `HttpClient` (`InfsDependencyInjection.cs:122-128`) has
  **no redirect hardening** — harmless while `CbslPriceReportClient` throws, but mirror Dambulla's
  `AllowAutoRedirect = false` — `agri-security.md:84`.
- **Future Excel path risk:** an Excel lib (**`openpyxl`, never `xlrd`**) would be the service's **first
  XML-container parser** — XXE / entity-expansion and small-file cell-explosion zip-bombs become real. **A byte
  cap alone is insufficient: cap workbook dimensions post-open.** 8 concrete acceptance criteria live on ClickUp
  `86cahefby` (2026-07-03 analysis comment) — attach them to the build, don't rediscover them —
  `agri-security.md:77`.
- **4 approved additions beyond those 8** (`agri-security.md:83`): PDF **page-count cap ~50 post-open** (the
  25MB byte cap misses small-but-explosive PDFs — **HARTI lacks this too**); cache filenames from **parsed
  dates, never URL basenames**; **SHA-256 recorded per artifact**; a **global FastAPI
  `@app.exception_handler(Exception)` backstop**.
- **P2 (festival calendar) was a security no-op** *iff* the seed stayed as in-migration `HasData` literals — it
  stops being a no-op the moment a seed loader reads a config-supplied file path — `agri-security.md:76`.
- **Security docstrings that overclaim are themselves BLOCKING** — a comment saying "redirect targets are
  re-validated" when the code doesn't do it will pass future reviews by inspection — `agri-reviewer.md:50`.

---

## 10. Project-specific test and review gates

These are gates tied to *this* project's failure modes, not generic testing advice.

### 10.1 Standing test invariants

- 🔴 **Leakage-by-truncation (the gold standard):** rebuild features with **all future data removed** and assert
  they are **bit-identical** — proven at max diff **0.00e+00**. Make it a standing test — `agri-qa.md:41`.
  **But see [C-4](#c-4-does-testleakagebytruncation-auto-cover-new-feature-columns): it does NOT auto-cover
  kwargs-attached columns.**
- 🔴 **SEED-COVERAGE vs TRAINING-RANGE** — for any calendar/reference table feeding features, assert it covers
  every year of the actual training history (`MIN(seed date) <= MIN(training date)`, no missing years).
  **Nothing else catches a forward-only seed**: CV can't (a degenerate feature just gets zero importance), and
  the truncation test passes trivially. Described as "the highest-value test for any reference-data phase" —
  `agri-qa.md:50`; **blocking** at review — `agri-reviewer.md:52`.
- **All-zero / constant-variance guard:** assert new feature columns have `std > 0` and a minimum non-default
  fraction **over the REAL training matrix** — the companion to seed-coverage — `agri-qa.md:51`.
- **Freeze-time test:** monkeypatch/freeze "now" to two values, assert output for a fixed historical date is
  unchanged — **no `today()` / `now()` / `utcnow()` in feature paths** — `agri-qa.md:52`.
- **Boundary pins for event-day pairs** (e.g. Avurudu Apr 13+14): parametrized day-before / day-of / day-after
  assertions — off-by-ones silently shift the signal — `agri-qa.md:53`.
- 🔴 **Two-date confusion tripwire** — the highest-leverage test for vintage data: any entity with both
  `ReferenceDate` and `PublishedAt` gets a test proving a value is **NOT visible at `ReferenceDate`** and **IS
  visible at `PublishedAt`** (make them weeks apart in the fixture). **Write this against the FIRST draft of the
  attach function, before any other test** — `agri-qa.md:61`.
- **Publish-boundary pins:** visible at `PublishedAt = D`, invisible at `D-1`, and a later-published
  outlier-magnitude trap value must never appear at `D`. **Always include a dtype-mismatch variant (`[s]` vs
  `[us]` join keys)** — `pd.read_sql` reintroduces mixed units forever — `agri-qa.md:62`.
- **`assert_first_vintage_precedes_training_start(...)`** — year-set coverage misses a mid-year-start series;
  compare `MIN(PublishedAt)` **per series** against `MIN(training date)` — `agri-qa.md:63`.
- **Rebase-seam guard is hermetic-only** — a base-year change is too rare to appear in live data; synthesise the
  seam and assert YoY is **NaN across it, never a spliced number**. Also assert **flat carry-forward between
  vintages (no `.interpolate()`)**, **NaN-not-0** when a series is absent (the deliberate opposite of
  `_attach_policy`'s 0), and the **~60-day staleness cap → NaN** — `agri-qa.md:64`.
- **Verified-profile exclusion:** an `IsVerified=0` (or absent) `CropAgronomyProfiles` row → the crop is absent
  from the training frame **AND** `/predict` degrades explicitly. Assert **both** sides; **an unverified crop
  leaking into training is a blocking failure** — `agri-qa.md:81`.
- **Seed stability:** `HasData` reference seeds (CropCategories) use fixed GUIDs + fixed `CreatedAt` — assert a
  re-run of `migrations add` produces an **EMPTY diff** (the `UtcNow`-churn regression) — `agri-qa.md:84`.
- **Label invariance across a schema move:** after the Crops → CropAgronomyProfiles column move (values
  unchanged), assert the built training frame is **bit-identical** — `agri-qa.md:85`.
- **Fold-corridor smoke test (the P4 standard):** against the promoted per-fold MAEs, assert winning folds
  (**1 & 3 for v10**) don't regress **>10%** and fold-2's loss margin doesn't worsen. **Pre-merge smoke, NOT a
  promotion-gate change** — `agri-qa.md:74`.

### 10.2 Test-suite mechanics (project-specific)

- **Hermetic-first (~90%)**: all boundary/seam/staleness tests on synthetic frames — `test_merge_asof_dtype.py`
  is the precedent (zero DB calls). **DB-gated tests only** for real-matrix variance/coverage, via the
  **`_db_or_skip()`** pattern — `agri-qa.md:65`.
- ⚠️ **`test_phase3.py` pollutes the suite** — run it **in isolation only, never edit it**; develop new test
  files standalone before trusting full-suite counts — `agri-qa.md:54`, `agri-qa.md:65`.
- **Reusable helpers already written**, in `test_festivals.py`: `assert_calendar_covers_years`,
  `assert_columns_not_constant`, `assert_no_wall_clock`, `assert_as_of_parity` — `agri-qa.md:75`.
  Write purity/coverage/parity checks as **generic reusable helpers**
  (`assert_feature_is_calendar_pure(fn, dates)`, `assert_calendar_seed_covers_training_range(...)`) so a later
  phase instantiates them per feature instead of rewriting — `agri-qa.md:54`.
- **Re-run claimed test baselines yourself from the REAL project venv** (`src/AgriForecast.ML/.venv`) — a
  builder-reported count that doesn't reproduce in your environment is **UNVERIFIED, not wrong**; say which and
  why — `agri-reviewer.md:51`.
- **"Zero-out-current-festival → coefficient unchanged" is ill-defined for XGBoost.** Operational version:
  persist the statistic as a standalone map (`residual_offsets` shape) and gate on recomputing it with the
  target festival masked — `agri-qa.md:72`.

### 10.3 Review red flags specific to this codebase

**Blocking** (`agri-reviewer.md:52`, `:59`, `:60`, `:61`, `:75-80`):
- Calendar/reference seed not spanning the full training history.
- **Two live definitions of the same feature** (e.g. a hardcoded `_is_festival` left in parallel with a new
  calendar table).
- An as-of join keyed on `ReferenceDate` instead of `PublishedAt` — **verify the actual
  `merge_asof(..., right_on=...)` argument, not the docstring**.
- Any backfill/default setting `PublishedAt = ReferenceDate` for a lagged-publication series.
- Outbound fetches bypassing the netguard choke points; new FastAPI ingest routes on bare `app`.
- **Dual-write / dual-definition of a series** (e.g. a second table claiming `USD_LKR`) — "the calendar-twin bug
  in DB form". Same for re-implementing features `_attach_policy` already emits.
- An ingest trigger on a bare-`[Authorize]` .NET controller (farmer-triggerable — no role auth exists).
- **`HasData` on `Crop` rows** — Crop GUIDs are per-database (auto-provisioned), so seeded GUIDs collide or
  duplicate across environments. Backfilling existing Crop rows = **name-keyed `migrationBuilder.Sql`**
  (`UPDATE ... WHERE Name IN (...)`), never seed data — `agri-dotnet.md:71`, `agri-reviewer.md:75`.
- **Blanket backfill `UPDATE`s** without per-`Source` scoping + before/after counts.
- **Parser targets extended without lockstep `CommodityAliases` rows** — demand the zero-unmapped-WARN evidence.
- **Agronomy columns re-added to `Crops`** or read from `Crops` after the `CropAgronomyProfiles` cut-over;
  registration paths creating `IsVerified=1` profiles; unverified-profile crops entering training or serving.
- **`GrowthPeriodDays` VALUE changed without owner sign-off**; conversely, a retrain triggered by a
  value-preserving schema move.
- **`CropCode` used as a key** (FK/index/join) or re-issued/re-coded after assignment.

**P4-specific red flags** (`agri-reviewer.md:69`): mixing `MarketPrices` and `PriceObservations` per-row for the
same market/day (~7.3% disagreement measured); "national" computed as a raw `AVG` instead of via
`get_feature_safe_market_ids()`; code assuming `ArrivalsKg` has data (0/52,755); `market_rank_pct` built from a
raw same-calendar-day join instead of already-as-of'd per-market columns; shrinkage/category priors computed on
full data instead of train-only per fold; per-horizon results *replacing* rather than *adding to* the flat
`metadata["cv"]` schema; torch/tensorflow appearing in `requirements.txt`; pickle side-cars outside the signed
payload.

**Method rule** (`agri-reviewer.md:49`): **check redirect handling on EVERY outbound-fetch path, not just the
primary one.** P1 blocker B1 — the listing scrape was guarded but the per-PDF download still auto-followed 3xx
unvalidated, which was the most attacker-influenceable path. Grep **every** `session.get` / `HttpClient`
call site for redirect policy, not just the one the diff highlights.

**Should-fix** (`agri-reviewer.md:62`, `:81`): YoY spanning a base-year rebase; missing staleness cap;
`ExistsAsync`/upsert not keyed on the full triple; new columns without `explain._LABELS` entries; EconomicCenters
deletion order; `serving/crop_categories.py` static map resurrected after the DB cut-over.

---

## 11. R2 data-foundation spec pins (owner-approved 2026-07-05 — binding)

Recorded in four files. These are **owner decisions**, not engineering preference.

- **`CropAgronomyProfiles` owns agronomy** (1:1, unique `CropId` FK): `GrowthPeriodDays`, `HarvestWindowDays`,
  Yala/Maha planting start/end month tinyints, `IsPerennial`, `DataSource` (citation), `VerifiedOn`,
  `IsVerified`. The three legacy `Crops` columns are being **moved there and then DROPPED** — **never add
  agronomy fields back onto `Crops`** — `agri-dotnet.md:72`.
- **Registration flows** (manual command **and** ingestion auto-provision) create a **pending profile
  (`IsVerified=0`)**, never a verified one — `agri-dotnet.md:72`.
- 🔴 **Crops without a VERIFIED profile (`IsVerified=1`) are EXCLUDED from forecasting.** This is the explicit,
  intended form of the old NULL-`GrowthPeriodDays` lockout (**Beans / Snake Gourd**) — **not a bug to "fix" by
  imputing a growth period** — `agri-ml-engineer.md:91`.
- `/predict` must **degrade explicitly** for such crops — a structured "not forecastable" response, **never** an
  upgrade to a confident-looking fallback — `agri-backend-dev.md:76`.
- **PlantingSeason encoding derives from the profile month columns**: **Year-round = 0 / Yala = 1 / Maha = 2** —
  not from a stored string — `agri-ml-engineer.md:92`.
- **Crop categories come from the DB `CropCategories` table**, read via the `load.py` pattern. The **hardcoded
  11-GUID map in `serving/crop_categories.py` is retired** — its docstring pre-authorises the DB as source of
  truth — **never resurrect a static twin** — `agri-ml-engineer.md:93`, `agri-backend-dev.md:75`.
- **`CropCategories` seed shape:** `HasData` with **fixed lowercase GUIDs + fixed `CreatedAt`**, per the
  `PolicyFlag` precedent — `agri-dotnet.md:71`.
- **Retrain happens ONCE, at plan Step 7** — `agri-ml-engineer.md:90`, `agri-dotnet.md:74`.

---

## 12. Cross-cutting gotchas worth memorising

1. **A forward-only calendar seed zeroes a feature for ~95% of training rows and CV still looks fine.** The
   worst class of bug in this project — `agri-data-engineer.md:48`.
2. **Two plausible date columns.** `ReferenceDate` vs `PublishedAt` — a copy-paste from a single-date attach
   function silently picks the wrong one — `agri-ml-engineer.md:67`.
3. **GUID case crosses a service boundary.** Uppercase silently missed a fallback dict; unit tests passed, HTTP
   caught it — `agri-backend-dev.md:42`.
4. **A static-Python twin of a DB table is a dual source of truth** and will drift (the `_is_festival` Apr 12–15
   vs calendar 13–14 case) — `agri-backend-dev.md:50`.
5. **NaN vs 0 is a per-source semantic**, not a style choice: macro NaN = "not knowable"; policy 0 = "no active
   policy" — `agri-ml-engineer.md:71`.
6. **Security choke points are per-call-site, not global** — every new fetcher can bypass them — `agri-security.md:82`.
7. **An env var that replaces rather than extends a defaults list** (`AGRI_INGEST_ALLOWED_HOSTS`) — `agri-security.md:75`.
8. **A "backfill X on migration" checklist item is vestigial if the table is net-new** — verify whether legacy
   rows exist before carrying it; the rule usually belongs in the ingestion fallback, not the migration —
   `agri-dotnet.md:58`.
9. **`CbslPriceReportIngestionService` / `CbslPriceReportClient` = the template for not-yet-buildable sources:**
   a throw-don't-guess client plus a feature-flag Disabled state that is a **documented no-op, never a false
   failure** — `agri-dotnet.md:59`.
10. **Check for already-shipped features before building a specced one** — P3 respecced flags that already
    existed verbatim — `agri-ml-engineer.md:72`.
11. **`.venv` console-script shebangs are stale — always `python -m`** — `agri-data-analyst.md:17`.
12. **Analysis is read-only:** never modify production modules, tests, the registry, or the DB (SELECT only) —
    `agri-data-analyst.md:26`.
13. **"The data can't tell" is a valid finding.** Confounds to name explicitly: festival ≈ season ≈ policy timing
    — `agri-data-analyst.md:27`.
14. **Show the denominator; segment before you average** — pooled means across the bimodal crop population
    mislead — `agri-data-analyst.md:23`.

---

## 13. Provenance and completeness

**Sources (all nine, read in full 2026-08-25):**

| File | Lines | Unique knowledge it held |
|---|---|---|
| `agri-backend-dev.md` | 108 | Python serving path internals; `load.py` house pattern; `admin_router` rules; `_SERVABLE_ML_KINDS`; `.NET` client tolerance shape |
| `agri-data-analyst.md` | 34 | Table-by-table data profile (2026-07-04); feature-safe markets; bimodal crop history; the `.env` / `python -m` connect recipe. **Only file with no coordination block.** |
| `agri-data-engineer.md` | 113 | HARTI + CBSL corpus reality; measured table-splice numbers; calendar/festival domain rules; alias lockstep; per-source backfill rule |
| `agri-dotnet.md` | 106 | Repo path + iCloud retirement; DB/Docker access; layer + CQRS conventions; migration hazards; entity templates; R2 schema pins |
| `agri-ml-engineer.md` | 124 | The `Real stack` correction + the "no Prophet/LSTM code" grep; label definition; feature-anchoring rules; vintage rules; P4 decisions |
| `agri-qa.md` | 116 | Project-specific test invariants; the self-correction on truncation-test coverage; measured backfill counts; suite mechanics |
| `agri-reviewer.md` | 112 | The blocking-check list per phase; redirect-path lesson; overclaiming-docstring rule; venv re-verification rule |
| `agri-security.md` | 91 | Full audit register F-01..F-14 with file:line; the secrets self-correction; shipped controls + commit `e07b4d4`; allowlist foot-gun; dependency policy |
| `agri-uiux.md` | 76 | Frontend repo/stack/port; stale-scaffold state; API drift warning; ClickUp Phase 4 IDs; the honest-uncertainty product principle |

**Method notes:**
- Byte-identity of the three copies (retired, live `~/.claude/agents/`, and in-repo
  `Project/Agri_Forecast/.claude/agents/`) confirmed with `diff -q` on 2026-08-25 — all nine identical in all
  three locations.
- The MLflow/ensemble contradiction inventory in §1.2 came from a case-insensitive grep across all nine files,
  **with a control term** (`agriforecast`) that returned non-zero in all nine (4–10 hits each), so the zero-hit
  files in the MLflow grep are real zeros and not a broken search.
- Paths spot-checked against the filesystem on 2026-08-25: `src/` project list, `src/AgriForecast.ML/`
  contents (incl. `harti_cache`, `cbsl_cache`, `models`, `experiments`, `tests`), `Project/UI/ForecastUI`, and
  the `<MEM>` directory. **No code, config or `CLAUDE.md` was modified.**

**Known gaps in this rescue — named, not papered over:**
- **Nothing here is re-verified against current code.** Every status claim is as-at its note date (2026-06-23 to
  2026-07-09); this file was written 2026-08-25, roughly seven weeks later. Model A may have been promoted;
  security findings may have been closed; P4/P5 may have shipped. **The files do not say, and I did not check.**
- **[C-5](#c-5-is-the-_build_x-duplication-fixed) is unresolved** — two same-day sources disagree on whether the
  `_build_X` dedup shipped.
- **No `CLAUDE.md` exists** in the AgriForecast repo (searched to depth 4, 2026-08-25). So these nine definitions
  were, in practice, the project's written record of its own state. That is why this file exists.
- Generic craft content was deliberately **not** carried; see §14.

---

## 14. Deliberately not carried over

Excluded as generic engineering craft that the portable fleet already owns, and which is derivable without this
project:

- General leakage theory, why walk-forward/`TimeSeriesSplit` beats K-fold, "fit scalers on train only"
  (`agri-ml-engineer.md:21-22`, `agri-reviewer.md:15`) — **except** where a project-specific instantiation
  exists, which is kept in §10.
- Generic "honest baselines", "set seeds", "report real numbers" (`agri-ml-engineer.md:24-31`).
- Generic FastAPI advice: typed Pydantic schemas, async for I/O, `/health`, no secrets in code
  (`agri-backend-dev.md:16-28`).
- Generic data-engineering hygiene: validate at ingestion, explicit missingness, idempotent scripts
  (`agri-data-engineer.md:16-27`).
- Generic QA process: find untested paths, use pytest, report actual output, don't approve on vibes
  (`agri-qa.md:23-29`).
- Generic review process: cite `file:line`, classify Blocking/Should-fix/Nit, a clean review is valid
  (`agri-reviewer.md:23-28`).
- Generic security method: OWASP mapping, threat-model first, minimal fixes, dependency scanning commands
  (`agri-security.md:16-37`) — **except** the concrete findings and shipped controls, kept in §9.
- Generic UI/UX process: design-system-before-screens, four async states, WCAG 2.2 AA, Lighthouse targets,
  small-dependency justification (`agri-uiux.md:25-39`) — **except** the farmer-context constraints and the
  honest-uncertainty principle, kept in §6.5, which are domain facts rather than craft.
- The role/ownership prose of each definition ("You are a senior X on AgriForecast...") — superseded by the
  portable fleet's own role definitions. **Ownership *boundaries* that resolve real conflicts** (who owns
  `/predict` vs `ForecastController`, ingestion is .NET not Python) **were kept** in §3 and §4.1, because those
  are project facts, not role descriptions.

Also **not** carried as knowledge, but recorded as a caution: the eight hard-coded `<MEM>` absolute paths (§2.4).
