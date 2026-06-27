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

## Train / retrain Model A
```bash
./.venv/bin/python train_model_a.py
```
Each run: builds features -> purged walk-forward CV -> gates against the BEST
naive baseline (min of carry-forward & per-crop mean) -> registers a NEW
version dir -> conditionally moves the `promoted.json` pointer.

### Retrain safety / rollback guardrail
Retraining is **safe to run repeatedly** — it never makes the live predictor
worse:

1. Every run registers a new `models/v<N>/` for history (audit trail).
2. The `promoted.json` pointer only moves when the new candidate is **strictly
   better than what is currently live**. "Better" is the CV MAE of whichever
   predictor each version actually serves: the ML model's MAE if it beat the
   baseline, else the crop-mean fallback MAE. The candidate must clear BOTH the
   best naive baseline (the existing gate) AND the live version's recorded
   served MAE (`metadata.json -> cv`).
3. If the candidate is not strictly better, the previously-promoted version
   stays active (no-op promotion / rollback). The crop-mean fallback behavior is
   preserved when no model beats the baseline.
4. Idempotent-safe: re-running on unchanged data just appends history versions;
   the pointer does not move.

Guardrail code: `agriforecast_ml/train/model.py` — `_promotion_decision()` /
`_served_mae()`; registry helper `load_promoted_metadata()` in
`agriforecast_ml/registry/registry.py`.

### Retrain cadence (for the daily Docker routine)
- **Recommended: weekly**, or **on-N-new-labelled-rows** (e.g. >= ~200 new
  harvest-labelled rows), whichever comes first. With ~13 months of data a
  daily retrain on barely-changed data is wasteful — the model will not suddenly
  beat the per-crop mean, and the guardrail will (correctly) refuse to promote.
- A **single manual retrain** is always safe to run on demand; the guardrail
  guarantees it can only improve or no-op the live predictor.
- The daily Docker container may *invoke* the retrain step, but it should be
  rate-limited (skip if last successful train < 7 days ago / insufficient new
  rows). Promotion happens automatically the moment the ML model earns it.

## Database
Connection is resolved (in order): environment variables `AGRI_DB_*`, then the
.NET API's `appsettings.json` connection string. No secrets are committed here.
