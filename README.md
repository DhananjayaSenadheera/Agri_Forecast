# AgriForecast

Harvest-price forecasting and crop recommendation for Sri Lankan farmers.

A farmer choosing what to plant is really making a bet on what the price will be at *harvest*,
months from now — not what it is today. AgriForecast answers that question directly: given a crop
and a planting date, it forecasts the market price at harvest, with a confidence interval and a
plain-language explanation of what is driving the number.

The system ingests daily wholesale prices, weather, macroeconomic indicators and agricultural news
from public Sri Lankan and international sources, engineers leakage-safe features from them, and
serves predictions from a gated XGBoost model that only goes live when it provably beats the
naive baseline it is replacing.

---

## Contents

- [Architecture](#architecture)
- [Data sources](#data-sources)
- [The forecasting model](#the-forecasting-model)
- [Tech stack](#tech-stack)
- [Getting started](#getting-started)
- [API surface](#api-surface)
- [Testing](#testing)
- [Security posture](#security-posture)
- [Repository layout](#repository-layout)

---

## Architecture

Three processes over one SQL Server database, split by responsibility rather than by layer:

```
   ┌──────────────────────────────────────────────────────────────────┐
   │  PUBLIC DATA SOURCES                                             │
   │  HARTI bulletins · CBSL price + macro reports · Dambulla DEC     │
   │  Open-Meteo weather archive · FX rates · agricultural news RSS   │
   └───────────────────────────┬──────────────────────────────────────┘
                               │  daily pass, per-source audit + watermarks
                               ▼
   ┌──────────────────────────────────────────────────────────────────┐
   │  AgriForecast.Ingestion  (.NET worker service)                   │
   │  PDF parsing · HTTP scraping · dedup · verification battery      │
   └───────────────────────────┬──────────────────────────────────────┘
                               ▼
                    ┌────────────────────┐
                    │  SQL Server        │  prices, weather, macro, news,
                    │  AgriForecast DB   │  crops, festivals, audit trail
                    └─────┬────────┬─────┘
                          │        │
        reads/writes ─────┘        └───── reads (features + labels)
              │                                   │
              ▼                                   ▼
   ┌────────────────────────┐        ┌──────────────────────────────┐
   │  AgriForecast.API      │  HTTP  │  AgriForecast.ML             │
   │  .NET 9 · Clean Arch   │ ─────► │  FastAPI · XGBoost · SHAP    │
   │  CQRS · JWT · RBAC     │        │  feature build · train · serve│
   └────────────────────────┘        └──────────────────────────────┘
              │
              ▼
        client applications
```

**Why the split.** The .NET API owns the domain, the users and the audit trail; the Python service
owns feature engineering, training and inference. They talk over HTTP rather than sharing a
process, so the model can be retrained, versioned and rolled back without touching the API, and the
API can serve cached forecasts when the ML service is down.

The .NET side follows Clean Architecture — `Domain` has no outward dependencies, `Application`
holds CQRS handlers and interfaces, `Infrastructure` implements them, `API` is transport only.

---

## Data sources

Seven ingestion sources, each with its own run audit row per pass:

| Source key | What it provides | Format |
|---|---|---|
| `HARTI` | Daily wholesale and retail prices across multiple markets | PDF bulletins |
| `CBSL` | Central Bank daily price report | PDF |
| `CBSL_MACRO` | CCPI inflation and macro indicator series | PDF press releases |
| `DAMBULLA_DEC` | Dambulla Dedicated Economic Centre prices | web portal |
| `WEATHER` | Historical daily weather for the growing region | Open-Meteo archive API |
| `ECONOMIC` | Exchange rates | open.er-api.com |
| `NEWS` | Agricultural news articles, scored for sentiment and topic | RSS + VADER |

Ingestion is designed to run unattended:

- **Per-source fail isolation** — a broken source fails its own run row and the pass continues.
- **Resumable watermarks** on `HARTI`, `CBSL` and `CBSL_MACRO` so a re-run picks up where it stopped.
- **Batch IDs** shared across a pass, so any day's ingestion can be reconstructed after the fact.
- **A 14-check verification battery** runs after ingestion and flags gaps, duplicates and stale series.
- **Crash detection** — an unfinished run older than the staleness window is reported as stopped, not running.

---

## The forecasting model

**Target.** For an observation on date *D*, the label is the market price at *D + GrowthPeriodDays*
for that crop — the price the farmer actually realises at harvest.

**Leakage discipline.** Every feature for date *D* uses only data known on or before *D*. The label
is the sole field permitted to look forward, and exists only during training. Cross-validation is
**purged walk-forward**: each fold drops the training rows whose label window overlaps the test
period, so no fold can learn from its own future.

**Features** include lagged prices (7/30/60/90 day), rolling means and volatility, momentum and
z-scores against a 90-day norm, weather, macro indicators, news sentiment, and Sri Lanka-specific
seasonality — Maha and Yala cultivation seasons, Poya days, and festival demand windows around
Avurudu and Christmas read from a seeded national calendar rather than hardcoded.

**Model.** Pooled XGBoost quantile regression, fitted in log space, producing a p50 point forecast
plus prediction intervals. Recency weighting uses a half-life tuned on an inner purged split inside
each training fold — never on the fold's own test window.

**Thin-history crops fall back.** Crops without enough labelled history are served by a per-crop
mean rather than the model, and the served predictor is chosen per crop. `/crop-readiness` exposes
which crops are model-backed so the UI can colour them honestly.

**Promotion is gated.** Every training run registers a new version directory for the audit trail,
but the live `promoted.json` pointer moves only when the candidate is strictly better than **both**
the best naive baseline (carry-forward and per-crop mean) **and** the currently-live version's
recorded served MAE. Retraining is therefore safe to run repeatedly — it can improve the live
predictor or no-op, never degrade it.

**Explanations.** SHAP `TreeExplainer` runs against the p50 model and maps raw feature names to
farmer-readable drivers ("recent 30-day price trend", "Avurudu demand", "days until the next
festival"). Explanation failure is non-fatal — the service falls back to a static explanation
rather than failing the prediction.

Full ML documentation, including retrain cadence and the rollback guardrail, is in
[`src/AgriForecast.ML/README.md`](src/AgriForecast.ML/README.md).

---

## Tech stack

**Backend** — .NET 9 · ASP.NET Core · EF Core 9 (SQL Server) · MediatR 13 (CQRS) ·
FluentValidation · JWT bearer with refresh tokens · Swashbuckle/Swagger

**ML** — Python 3.12 · XGBoost · scikit-learn · SHAP · pandas · SQLAlchemy · FastAPI · uvicorn ·
pdfplumber (PDF ingestion) · feedparser + vaderSentiment (news)

**Testing** — xUnit · Moq · FluentAssertions · pytest

**Infrastructure** — Docker (ingestion worker and ML service) · SQL Server

---

## Getting started

### Prerequisites

- .NET 9 SDK
- Python 3.12+
- SQL Server (local Docker instance is fine)

### 1. Database and configuration

No secrets are committed. Copy the example settings and supply your own values:

```bash
cp src/AgriForecast.API/appsettings.Development.example.json \
   src/AgriForecast.API/appsettings.Development.json
```

Then set the sensitive values with user secrets:

```bash
cd src/AgriForecast.API
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost,1433;Database=AgriForecast;User Id=sa;Password=<your-password>;TrustServerCertificate=True;"
dotnet user-secrets set "Jwt:Key" "<at least 32 random bytes>"
dotnet user-secrets set "MlService:AdminApiKey" "<shared key, matches ML_ADMIN_API_KEY>"
```

The API **fails at startup** if `Jwt:Key` is missing, empty, or shorter than 32 bytes — this is
deliberate, so a misconfigured deployment cannot silently run with a weak signing key.

### 2. Run the API

```bash
dotnet restore src/src.sln
dotnet ef database update --project src/AgriForecast.Infrastructure --startup-project src/AgriForecast.API
dotnet run --project src/AgriForecast.API
```

Swagger UI is served at `/swagger`. An admin account is bootstrapped on first start.

### 3. Run ingestion

```bash
# one pass, then exit
RUN_ONCE=true dotnet run --project src/AgriForecast.Ingestion

# or continuously, one pass every 24 hours
dotnet run --project src/AgriForecast.Ingestion
```

### 4. Build features, train and serve

```bash
cd src/AgriForecast.ML
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt

python build_features.py        # populate CropFeatureDaily
python train_model_a.py         # CV, gate, register, conditionally promote
uvicorn agriforecast_ml.serving.app:app --port 8077
```

Or with Docker:

```bash
docker build -t agriforecast-ml src/AgriForecast.ML
docker run -p 8077:8077 -v $(pwd)/models:/app/models \
  -e AGRI_DB_HOST=... -e AGRI_DB_PASSWORD=... \
  -e ML_ADMIN_API_KEY=... agriforecast-ml
```

The model registry is not baked into the image — mount a trained `models/` directory at runtime.

---

## API surface

All routes require a bearer token unless noted; `[Admin]` marks role-restricted routes.

| Area | Route | Purpose |
|---|---|---|
| Auth | `/api/auth` | Register, login, refresh, logout *(public)* |
| Forecast | `/api/forecast/monthly/{cropId}` | Monthly price forecast series |
| | `/api/forecast/crop/{cropId}/harvest` | Harvest-price forecast for a planting date |
| | `/api/forecast/crop/{cropId}/timeline` | Forecast timeline for a crop |
| | `/api/forecast/crop-readiness` | Which crops are model-backed vs fallback |
| Crops | `/api/crops` | Crop catalogue and agronomy profiles `[Admin]` for writes |
| Markets | `/api/markets` | Markets and market overview |
| Prices | `/api/prices` | Price history |
| Indicators | `/api/indicators` | Macroeconomic indicator series |
| Festivals | `/api/festival-calendar` | National festival calendar `[Admin]` for writes |
| News | `/api/news-events`, `/api/news-articles` | Ingested news and derived events |
| Policy | `/api/policy-flag` | Policy interventions affecting prices |
| Admin | `/api/admin/ingestion` | Ingestion status, runs, manual triggers `[Admin]` |
| | `/api/admin/logs` | System error log `[Admin]` |
| | `/api/users` | User administration `[Admin]` |

**ML service** (port 8077) — `/health`, `/model-info`, `/crop-readiness`, `/predict`, `/timeline`
are public by design; every `/admin/*` route requires an `X-API-Key` header.

---

## Testing

```bash
dotnet test src/src.sln                          # 43 xUnit test suites
cd src/AgriForecast.ML && pytest                 # 37 pytest suites
```

Coverage spans domain rules, CQRS handlers, validators, auth and refresh-token flows,
authorization wiring, the global exception middleware, ingestion audit and deduplication, PDF
parsers against committed fixture documents, news sentiment, reason-code contracts, and
directional forecast accuracy.

---

## Security posture

- **No secrets in the repository.** `appsettings.json` ships with empty credential fields and
  explanatory comments; values come from user secrets or environment variables. The Python side
  reads the same connection string rather than duplicating it.
- **Fail-closed by default.** Rate limiting is always on (100 req/min globally, 10 req/min on auth
  routes). CORS allows no origins until explicitly configured. Forwarded headers are off unless
  trusted proxy IPs are listed, so the rate limiter cannot be collapsed into one shared bucket.
- **Refresh tokens use a separate audience**, so a refresh token is always rejected by the
  access-token pipeline.
- **Admin API key comparison is constant-time**, and a missing key fails closed rather than
  silently disabling auth.
- **Dependency floors are security floors**, verified with `pip-audit`; PDF-parsing dependencies
  are treated as an untrusted-input path and pinned accordingly.
- Errors are sanitised before they reach clients; a global exception middleware logs the detail.

---

## Repository layout

```
src/
├── AgriForecast.Domain/          entities, enums, repository interfaces — no dependencies
├── AgriForecast.Application/     CQRS commands, queries, handlers, validators, mappers
├── AgriForecast.Infrastructure/  EF Core, ingestion services, read stores, forecasting client
├── AgriForecast.API/             controllers, middleware, startup, auth wiring
├── AgriForecast.Ingestion/       daily worker service (Docker)
├── AgriForecast.ML/              Python: features, training, registry, FastAPI serving (Docker)
└── AgriForecast.Tests/           xUnit test suite
```

Development happens on feature branches merged into `main` by pull request.

---

## Author

**Dhananjaya Senadheera** — Software Engineer (Cloud & .NET)
[LinkedIn](https://www.linkedin.com/in/dhananjaya-senadheera/) · [GitHub](https://github.com/DhananjayaSenadheera)
