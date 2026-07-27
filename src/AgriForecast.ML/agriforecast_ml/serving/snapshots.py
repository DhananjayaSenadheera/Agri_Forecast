"""Forecast snapshots: the nightly snapshot pass and the maturing pass.

One row per crop per day in ForecastSnapshots: what we forecast on that day, and -
once the harvest date arrives - what the price actually turned out to be. It is the
evidence base for the admin "Forecast accuracy" surface and for the farmer portfolio's
prediction history.

Three laws this module exists to keep:

1. WRITE-ONLY SERVING ARTIFACT. ForecastSnapshots is never read by load.py or
   features.py. A snapshot fed back into training is a self-referential lookahead: the
   model would be learning from its own past outputs. This module is the only Python
   writer, and it lives under serving/ (not next to the loaders) so the separation is
   structural, not just a convention. A static guard test enforces it.

2. FROZEN PREDICTIONS. The snapshot pass writes the prediction columns once. The
   maturing pass only ADDS actual/error columns - it never rewrites PredictedPrice,
   LowerBound, UpperBound, Confidence, ActivePredictor or ModelVersion. Scoring a
   forecast you quietly re-made with hindsight measures nothing. A re-run of the
   snapshot pass over an already-matured day is likewise refused (the MERGE only
   updates rows still in a pre-maturity state).

3. SERIES IDENTITY. The matured actual comes from predict._last_avgprice_at_or_before -
   literally the same function the serving carry-forward anchor uses - so "what we
   predicted" and "what we scored it against" can never drift onto two different
   definitions of the price.

Fail-soft and report-only (PRD 3.7): one crop's failure is counted and the pass
continues; the pass never gates ingest, verify or training.
"""
from __future__ import annotations

import logging
import uuid
from datetime import date, datetime, timedelta, timezone

import pandas as pd
from sqlalchemy import text

from ..db import get_engine
from ..features import _FFILL_LIMIT
from . import predict

_log = logging.getLogger(__name__)

TABLE = "ForecastSnapshots"

# How far back the maturing pass may reach for the harvest-day price. Bound to the
# label's own forward-fill limit ON PURPOSE and by reference, not by a copied literal:
# the label at plant date D is the ffilled AvgPrice at D+gp, which is exactly "the newest
# trading price within _FFILL_LIMIT days at or before D+gp". If that constant ever moves,
# the scoring window moves with it instead of silently disagreeing with the label.
SNAPSHOT_MATCH_BACK_DAYS = _FFILL_LIMIT

# Documented latency rationale (not a branch condition). Prices for day H are typically
# published and ingested within about a week: a row whose harvest date has just passed and
# has no actual yet is normal, not a problem, for roughly this long. It is the "do not
# panic yet" number behind the retry-nightly behaviour, and the admin surface uses it to
# describe a still-pending row honestly.
SNAPSHOT_MATURE_GRACE_DAYS = 7

# Terminal give-up line. After this many days past the harvest date with still no price in
# the feature store, the row is marked actual_unavailable: counted, surfaced, and excluded
# from accuracy - never quietly dropped, and never scored against a faked number.
SNAPSHOT_UNAVAILABLE_AFTER_DAYS = 14

# MaturityState vocabulary (persisted strings - the .NET read side matches on them).
MATURITY_PENDING = "pending"
MATURITY_MATURED = "matured"
MATURITY_ACTUAL_UNAVAILABLE = "actual_unavailable"
MATURITY_NOT_MATURABLE = "not_maturable"

# Mirrors regression_metrics' MAPE denominator clip, so a percentage error computed here
# and one computed by train/evaluate.py agree exactly.
_PCT_ERROR_CLIP = 1e-6


def _to_naive_utc(value: datetime) -> datetime:
    """Naive UTC for a datetime2 bind. A tz-aware bind does not raise, it silently
    no-ops in production, so every datetime this module sends goes through here."""
    if value.tzinfo is not None:
        return value.astimezone(timezone.utc).replace(tzinfo=None)
    return value


def _now_utc() -> datetime:
    return _to_naive_utc(datetime.now(timezone.utc))


def _as_date(value) -> date | None:
    """Coerce a DB/pandas date-ish value to a plain date. None-safe."""
    if value is None:
        return None
    if isinstance(value, datetime):
        return value.date()
    if isinstance(value, date):
        return value
    ts = pd.Timestamp(value)
    if pd.isna(ts):
        return None
    return ts.date()


def _percentage_error(signed_error: float, actual: float) -> float:
    """Signed percentage error with regression_metrics' 1e-6 denominator clip.

    Signed (not absolute) so the stored column can be aggregated into either MAPE or a
    directional bias without re-deriving anything.
    """
    return signed_error / max(abs(float(actual)), _PCT_ERROR_CLIP) * 100.0


# --------------------------------------------------------------------------------------
# DB primitives. Every statement this module issues lives in one of these four functions,
# so the passes below are pure logic and the tests can drive them without a database.
# --------------------------------------------------------------------------------------

# Crops has no IsActive column (checked against the entity + live schema): every row in
# Crops is an active crop, so "for every active crop" is the whole table. A crop with no
# verified agronomy profile still gets a snapshot row - predict_harvest serves it a
# fallback with no horizon and the row is recorded not_maturable, which is the honest
# record of "we had no harvest date to score", not an omission.
_ACTIVE_CROPS_SQL = text("SELECT Id, Name FROM Crops ORDER BY Name")


def _active_crops(engine) -> list[dict]:
    """[{cropId, cropName}] for every crop, GUIDs lowercased for the wire."""
    with engine.connect() as conn:
        df = pd.read_sql(_ACTIVE_CROPS_SQL, conn)
    return [{"cropId": str(r.Id).lower(), "cropName": r.Name}
            for r in df.itertuples(index=False)]


# Atomic upsert on the (CropId, SnapshotDate) unique key, with the frozen-prediction rule
# expressed in the MATCHED predicate: only a row that has not reached a terminal maturity
# state may have its prediction columns rewritten. A matured or actual_unavailable row is
# left exactly as it was - re-running the pass over an old date cannot re-predict history.
# OUTPUT $action reports what actually happened, so inserted/updated counts are observed,
# never assumed (a frozen row yields no output row and is counted as neither).
_UPSERT_SQL = text(f"""
MERGE {TABLE} WITH (HOLDLOCK) AS t
USING (SELECT :crop_id AS CropId, :snapshot_date AS SnapshotDate) AS s
    ON t.CropId = s.CropId AND t.SnapshotDate = s.SnapshotDate
WHEN MATCHED AND t.MaturityState IN ('{MATURITY_PENDING}', '{MATURITY_NOT_MATURABLE}')
    THEN UPDATE SET
        HarvestDate      = :harvest_date,
        GrowthPeriodDays = :growth_period_days,
        PredictedPrice   = :predicted_price,
        LowerBound       = :lower_bound,
        UpperBound       = :upper_bound,
        ReferencePrice   = :reference_price,
        Confidence       = :confidence,
        ActivePredictor  = :active_predictor,
        FallbackTier     = :fallback_tier,
        ModelVersion     = :model_version,
        ReasonCode       = :reason_code,
        MaturityState    = :maturity_state
WHEN NOT MATCHED THEN
    INSERT (Id, CropId, SnapshotDate, HarvestDate, GrowthPeriodDays,
            PredictedPrice, LowerBound, UpperBound, ReferencePrice,
            Confidence, ActivePredictor, FallbackTier, ModelVersion, ReasonCode,
            MaturityState, CreatedAtUtc)
    VALUES (:id, :crop_id, :snapshot_date, :harvest_date, :growth_period_days,
            :predicted_price, :lower_bound, :upper_bound, :reference_price,
            :confidence, :active_predictor, :fallback_tier, :model_version, :reason_code,
            :maturity_state, :created_at_utc)
OUTPUT $action;
""")


def _upsert_snapshot(engine, row: dict) -> str:
    """Insert or update one snapshot row. Returns 'INSERT', 'UPDATE' or 'FROZEN'.

    'FROZEN' means the row exists and has already reached a terminal maturity state, so
    the MERGE deliberately left it alone.
    """
    params = dict(row)
    params["id"] = str(uuid.uuid4())
    params["created_at_utc"] = _now_utc()
    with engine.begin() as conn:
        result = conn.execute(_UPSERT_SQL, params)
        actions = [str(r[0]).upper() for r in result.fetchall()]
    return actions[0] if actions else "FROZEN"


_PENDING_SQL = text(f"""
    SELECT Id, CropId, HarvestDate, PredictedPrice, LowerBound, UpperBound
    FROM {TABLE}
    WHERE MaturityState = '{MATURITY_PENDING}'
      AND HarvestDate IS NOT NULL
      AND HarvestDate <= :today
    ORDER BY HarvestDate
""")


def _pending_rows(engine, today: date) -> list[dict]:
    """Snapshot rows whose harvest date has arrived and that are still awaiting an actual."""
    with engine.connect() as conn:
        df = pd.read_sql(_PENDING_SQL, conn, params={"today": today})
    return [{"id": str(r.Id), "cropId": str(r.CropId).lower(),
             "harvestDate": _as_date(r.HarvestDate),
             "predictedPrice": float(r.PredictedPrice),
             "lowerBound": float(r.LowerBound),
             "upperBound": float(r.UpperBound)}
            for r in df.itertuples(index=False)]


# Touches actual/error columns only. The prediction columns are absent from this SET list
# BY LAW (PRD 3.2) - a static test asserts none of them ever appears here. The
# MaturityState re-check in the WHERE makes a concurrent or repeated pass a no-op rather
# than a re-maturing.
_MATURE_SQL = text(f"""
    UPDATE {TABLE}
       SET ActualPrice        = :actual_price,
           ActualObservedDate = :actual_observed_date,
           SignedError        = :signed_error,
           AbsoluteError      = :absolute_error,
           PercentageError    = :percentage_error,
           WithinInterval     = :within_interval,
           MaturityState      = '{MATURITY_MATURED}',
           MaturedAtUtc       = :matured_at_utc
     WHERE Id = :id AND MaturityState = '{MATURITY_PENDING}'
""")


def _write_maturity(engine, fields: dict) -> None:
    """Fill the actual/error columns of one row and mark it matured."""
    params = dict(fields)
    params["matured_at_utc"] = _now_utc()
    with engine.begin() as conn:
        conn.execute(_MATURE_SQL, params)


# Terminal give-up. MaturedAtUtc is stamped as the adjudication instant (when we stopped
# waiting), so the row is auditable; the actual/error columns stay NULL because there is
# no honest number to put in them.
_UNAVAILABLE_SQL = text(f"""
    UPDATE {TABLE}
       SET MaturityState = '{MATURITY_ACTUAL_UNAVAILABLE}',
           MaturedAtUtc  = :matured_at_utc
     WHERE Id = :id AND MaturityState = '{MATURITY_PENDING}'
""")


def _mark_unavailable(engine, row_id: str) -> None:
    with engine.begin() as conn:
        conn.execute(_UNAVAILABLE_SQL, {"id": row_id, "matured_at_utc": _now_utc()})


# --------------------------------------------------------------------------------------
# Snapshot pass
# --------------------------------------------------------------------------------------

def _snapshot_row(crop: dict, snapshot_date: date, payload: dict,
                  reference_price: float | None) -> dict:
    """Bind params for one crop's snapshot, straight from the /predict payload.

    GrowthPeriodDays and therefore HarvestDate come from the payload - the horizon the
    forecast was actually served for - never from a Crops/profile column read separately
    here, which could disagree with what was predicted. No growth period means no harvest
    date and no way to ever score the row: it is recorded not_maturable rather than
    dropped, so the pass's crop coverage stays honest.
    """
    gp = payload.get("growthPeriodDays")
    gp = int(gp) if gp is not None and not pd.isna(gp) else None
    harvest_date = snapshot_date + timedelta(days=gp) if gp else None
    served_harvest = payload.get("harvestDate")
    if harvest_date is not None and served_harvest and served_harvest != harvest_date.isoformat():
        # Should be impossible (predict_harvest derives it the same way); log rather than
        # silently persist a horizon that disagrees with what was served.
        _log.warning("Snapshot harvest date %s disagrees with served payload %s for crop %s",
                     harvest_date.isoformat(), served_harvest, crop["cropId"])
    return {
        "crop_id": crop["cropId"],
        "snapshot_date": snapshot_date,
        "harvest_date": harvest_date,
        "growth_period_days": gp,
        "predicted_price": float(payload["predictedPrice"]),
        "lower_bound": float(payload["lowerBound"]),
        "upper_bound": float(payload["upperBound"]),
        "reference_price": reference_price,
        "confidence": payload.get("confidence"),
        "active_predictor": payload.get("activePredictor"),
        "fallback_tier": payload.get("fallbackTier"),
        "model_version": payload.get("modelVersion"),
        "reason_code": payload.get("reasonCode"),
        "maturity_state": MATURITY_PENDING if harvest_date else MATURITY_NOT_MATURABLE,
    }


def _is_model_served_payload(payload: dict) -> bool:
    """True when the ML model produced this forecast, false for any fallback rung.

    Reuses serving's own _SERVABLE_ML_KINDS so a future ML kind is classified by the same
    list that decides whether to serve it. Model and fallback are counted separately and
    never blended (PRD 3.4).
    """
    return str(payload.get("activePredictor")) in predict._SERVABLE_ML_KINDS


def _snapshot_pass(engine, snapshot_date: date, *, dry_run: bool) -> tuple[dict, int]:
    """One forecast per crop for snapshot_date. Returns (summary, failure_count)."""
    crops = _active_crops(engine)
    inserted = updated = model_served = fallback_served = not_maturable = failures = 0

    for crop in crops:
        crop_id = crop["cropId"]
        try:
            payload = predict.predict_harvest(crop_id, snapshot_date)
            # The price that was known on the plant date, stored now so directional
            # accuracy is computable later without re-reading history (and without ever
            # being tempted to use a post-plant reference).
            reference_price = predict._carry_forward_price(crop_id, snapshot_date)
            row = _snapshot_row(crop, snapshot_date, payload, reference_price)
            if row["maturity_state"] == MATURITY_NOT_MATURABLE:
                not_maturable += 1
            if _is_model_served_payload(payload):
                model_served += 1
            else:
                fallback_served += 1
            if dry_run:
                continue
            action = _upsert_snapshot(engine, row)
            if action == "INSERT":
                inserted += 1
            elif action == "UPDATE":
                updated += 1
            else:
                _log.info(
                    "Snapshot for crop %s on %s left frozen: the row has already "
                    "matured, so its prediction is not rewritten.",
                    crop_id, snapshot_date)
        except Exception:
            # Fail-soft per crop: one bad crop must never cost the other 95 their row.
            failures += 1
            _log.exception("Snapshot failed for crop %s on %s", crop_id, snapshot_date)

    summary = {
        "snapshotDate": snapshot_date.isoformat(),
        "cropsAttempted": len(crops),
        "inserted": inserted,
        "updated": updated,
        "modelServed": model_served,
        "fallbackServed": fallback_served,
        "notMaturable": not_maturable,
        "modelVersion": (predict.model_info() or {}).get("version"),
    }
    return summary, failures


# --------------------------------------------------------------------------------------
# Maturing pass
# --------------------------------------------------------------------------------------

def _mature_pass(engine, today: date, *, dry_run: bool) -> tuple[dict, int]:
    """Score every snapshot whose harvest date has arrived. Returns (summary, failures)."""
    rows = _pending_rows(engine, today)
    matured = marked_unavailable = failures = 0
    max_harvest: date | None = None

    for row in rows:
        try:
            harvest_date = row["harvestDate"]
            hit = predict._last_avgprice_at_or_before(
                row["cropId"], harvest_date, SNAPSHOT_MATCH_BACK_DAYS)
            if hit is None:
                # Ingestion look-back means the harvest-day price can still land days
                # later, so a miss is a retry, not a verdict - until the give-up line.
                if (today - harvest_date).days > SNAPSHOT_UNAVAILABLE_AFTER_DAYS:
                    if not dry_run:
                        _mark_unavailable(engine, row["id"])
                    marked_unavailable += 1
                continue
            actual, observed = hit
            signed = row["predictedPrice"] - actual
            if not dry_run:
                _write_maturity(engine, {
                    "id": row["id"],
                    "actual_price": actual,
                    "actual_observed_date": _as_date(observed),
                    "signed_error": signed,
                    "absolute_error": abs(signed),
                    "percentage_error": _percentage_error(signed, actual),
                    "within_interval": bool(row["lowerBound"] <= actual <= row["upperBound"]),
                })
            matured += 1
            if max_harvest is None or harvest_date > max_harvest:
                max_harvest = harvest_date
        except Exception:
            failures += 1
            _log.exception("Maturing failed for snapshot row %s", row.get("id"))

    summary = {
        "scanned": len(rows),
        "matured": matured,
        # Rows still pending after this pass: everything not scored and not given up on,
        # which correctly includes rows that failed (they were left untouched).
        "stillPending": len(rows) - matured - marked_unavailable,
        "markedUnavailable": marked_unavailable,
        "maxHarvestDateMatured": max_harvest.isoformat() if max_harvest else None,
    }
    return summary, failures


def _empty_mature_summary() -> dict:
    """The mature block when the pass was not run - same keys, honest zeros."""
    return {"scanned": 0, "matured": 0, "stillPending": 0,
            "markedUnavailable": 0, "maxHarvestDateMatured": None}


# --------------------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------------------

def run(snapshot_date: date | str | None = None, *, run_mature: bool = True,
        dry_run: bool = False, today: date | None = None) -> dict:
    """Run the nightly snapshot pass and (by default) the maturing pass.

    snapshot_date defaults to today and is the plant date every forecast is made for.
    dry_run computes and counts everything but issues no write, so a caller can see what
    a pass would do. today is injectable for tests; production always uses the real date -
    the maturing give-up line must never be evaluated against an arbitrary clock.

    Returns the PRD 4.2 summary dict. Per-crop and per-row failures are counted in
    `errors` and never raise: this job is report-only and must not fail a pipeline.
    """
    engine = get_engine()
    if isinstance(snapshot_date, str):
        snapshot_date = date.fromisoformat(snapshot_date)
    snap_date = snapshot_date or date.today()
    as_of = today or date.today()

    snapshot_summary, snapshot_failures = _snapshot_pass(engine, snap_date, dry_run=dry_run)
    if run_mature:
        mature_summary, mature_failures = _mature_pass(engine, as_of, dry_run=dry_run)
    else:
        mature_summary, mature_failures = _empty_mature_summary(), 0

    return {
        "status": "ok",
        "snapshot": snapshot_summary,
        "mature": mature_summary,
        "errors": {
            "snapshotCropFailures": snapshot_failures,
            "matureRowFailures": mature_failures,
        },
    }
