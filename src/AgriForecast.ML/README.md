# AgriForecast.ML

Python ML project for AgriForecast — **a separate project inside the same solution**
(`src/`), run with its own virtualenv (not built by the .NET solution).

## Responsibilities
1. **Feature engineering** (`build_features.py`) — transforms raw `MarketPrices`,
   `WeatherRecords` and `Crops` into the model-ready `CropFeatureDaily` table.
2. (later) Model A training + a FastAPI prediction service.

## Setup
```bash
cd src/AgriForecast.ML
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
```

## Run feature build
```bash
python build_features.py
```

## Database
Connection is resolved (in order): environment variables `AGRI_DB_*`, then the
.NET API's `appsettings.json` connection string. No secrets are committed here.
