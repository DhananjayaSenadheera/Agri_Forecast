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
   definitions of the price. CropFeatureDaily stores trading days only, so
   ActualObservedDate is always a real trading day and audits exactly how far the price
   was carried.

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

# The publication window, and a load-bearing condition in _accepted_actual.
#
# The feature store typically ends at H-1 when the nightly pass runs, so a row whose
# harvest date is H would otherwise find an H-1 price inside the back-window and freeze
# it as "the harvest price" on the very first sweep - permanently, since matured rows are
# frozen. Day-over-day AvgPrice moves about 10% in the median, so that is a real error
# baked into every score.
#
# So: take the harvest day's own price whenever it is there, and otherwise WAIT this many
# days for it to be published before accepting a carried price. Only after the window has
# passed is a carry the honest best answer rather than an impatient one.
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


class SnapshotDateError(ValueError):
    """An out-of-range snapshotDate. Distinct from a pass failure: the caller asked for
    something we refuse to compute, so the API answers 422 rather than 502."""


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
#
# IF CROPS EVER GAINS IsActive OR ANY SOFT-DELETE FLAG, THIS SQL MUST FILTER ON IT.
# Silently snapshotting retired crops would pad the accuracy denominator with rows nobody
# can act on, and the pass would never look broken while doing it.
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
#
# not_maturable IS deliberately re-writable: a crop whose agronomy profile gets verified
# between two passes should stop being a dead row and start being scoreable. That
# promotion is only safe because run() refuses a snapshotDate older than
# SNAPSHOT_MATURE_GRACE_DAYS - without that bound, re-running an ancient date would
# re-make a long-past prediction with today's model and today's prices and store it as if
# it had been made back then. The date bound is what keeps this from being a time machine.
#
# The CASTs make the join types explicit: CropId is uniqueidentifier and SnapshotDate is
# date in the .NET schema, so binding bare strings/datetimes would rely on an implicit
# conversion that can also cost the unique index.
_UPSERT_SQL = text(f"""
MERGE {TABLE} WITH (HOLDLOCK) AS t
USING (SELECT CAST(:crop_id AS uniqueidentifier) AS CropId,
              CAST(:snapshot_date AS date) AS SnapshotDate) AS s
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
    VALUES (:id, s.CropId, s.SnapshotDate, :harvest_date, :growth_period_days,
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
    here, which could disagree with what was predicted. No growth period - or a
    nonsensical non-positive one - means no harvest date and no way to ever score the row:
    it is recorded not_maturable rather than dropped, so the pass's crop coverage stays
    honest.

    The interval is stored VERBATIM, never clamped or re-ordered: this row has to be the
    forecast the farmer was actually shown. The .NET CHECK constraints (UpperBound >=
    LowerBound >= 0) therefore act as a loud tripwire on a malformed served band rather
    than something to pre-empt by quietly rewriting the numbers.
    """
    gp = payload.get("growthPeriodDays")
    gp = int(gp) if gp is not None and not pd.isna(gp) else None
    harvest_date = snapshot_date + timedelta(days=gp) if gp and gp > 0 else None
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
    """One forecast per crop for snapshot_date. Returns (summary, failure_count).

    On a real (non-dry) pass every attempted crop lands in exactly one bucket:
    inserted + updated + frozen + failures == cropsAttempted. That identity is the point
    of reporting `frozen` at all - without it a re-run over already-matured days would
    report zeros for everything and look like it had done nothing wrong.
    """
    crops = _active_crops(engine)
    inserted = updated = frozen = model_served = fallback_served = 0
    not_maturable = failures = 0

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
                frozen += 1
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
        "frozen": frozen,
        "modelServed": model_served,
        "fallbackServed": fallback_served,
        "notMaturable": not_maturable,
        "modelVersion": (predict.model_info() or {}).get("version"),
    }
    return summary, failures


# --------------------------------------------------------------------------------------
# Maturing pass
# --------------------------------------------------------------------------------------

def _accepted_actual(hit, harvest_date: date, today: date):
    """The actual this row may be scored with, or None to leave it pending.

    Maturing is irreversible - the row freezes - so this decides once, and the deciding
    question is "is this the harvest price, or merely the newest price we happen to have?"

      * The harvest day's own price: score it, today, whatever the calendar says.
      * A carried price from before the harvest day: WAIT. The nightly pass runs while
        the feature store still ends around H-1, so accepting the carry immediately would
        freeze an H-1 price as the harvest price for essentially every row - and prices
        move ~10% day over day. Once SNAPSHOT_MATURE_GRACE_DAYS have passed the harvest
        day's price has had its publication window and is not coming; the carry is then
        the honest best answer, exactly as the label's own ffill would have it.
      * A non-positive price is not a scoreable harvest price at all: it would also blow
        the PercentageError column's range through the 1e-6 clip, and a row that fails to
        write retries every night forever. Refuse it here so it ages out through the
        normal give-up path instead.

    The rule is stated as a property of (observed, harvest_date, today) rather than as an
    ordering of checks, so it cannot change meaning depending on which branch runs first.
    """
    if hit is None:
        return None
    value, observed = hit
    observed_date = _as_date(observed)
    if value <= 0:
        _log.warning(
            "Ignoring non-positive AvgPrice %s at %s as a harvest actual for %s - "
            "not a scoreable price.", value, observed_date, harvest_date)
        return None
    if observed_date == harvest_date:
        return value, observed_date
    if (today - harvest_date).days >= SNAPSHOT_MATURE_GRACE_DAYS:
        return value, observed_date
    return None


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
            accepted = _accepted_actual(hit, harvest_date, today)
            if accepted is None:
                # Nothing acceptable yet. Ingestion look-back means the harvest-day price
                # can still land days later, so this is a retry, not a verdict - until the
                # give-up line, which sits past the grace window so a row that waited for
                # its exact day still gets its carry considered before we give up.
                if (today - harvest_date).days > SNAPSHOT_UNAVAILABLE_AFTER_DAYS:
                    if not dry_run:
                        _mark_unavailable(engine, row["id"])
                    marked_unavailable += 1
                continue
            actual, observed = accepted
            signed = row["predictedPrice"] - actual
            if not dry_run:
                _write_maturity(engine, {
                    "id": row["id"],
                    "actual_price": actual,
                    "actual_observed_date": observed,
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

def _validate_snapshot_date(snap_date: date, as_of: date) -> None:
    """Refuse a snapshotDate we cannot honestly stand behind.

    A FUTURE date would record a forecast as though it had been made on a day that has
    not happened, against features that do not exist yet - the snapshot's whole value is
    that it is a point-in-time record, and a future one is a fabrication.

    A date further back than SNAPSHOT_MATURE_GRACE_DAYS is refused for the mirror-image
    reason: re-predicting an old day with today's model and today's prices produces a
    "prediction" that had hindsight. The legitimate use is catching up a night the
    pipeline missed, which is days, not months. Genuine retrospective backfill is PRD 8.5
    open decision 5 and needs a schema marker column to label those rows as backtest
    rather than as predictions we actually made - so it is refused here rather than
    quietly allowed to contaminate the accuracy record.
    """
    if snap_date > as_of:
        raise SnapshotDateError(
            f"snapshotDate {snap_date.isoformat()} is in the future "
            f"(today is {as_of.isoformat()}); a forecast cannot be recorded as having "
            "been made on a day that has not happened.")
    age = (as_of - snap_date).days
    if age > SNAPSHOT_MATURE_GRACE_DAYS:
        raise SnapshotDateError(
            f"snapshotDate {snap_date.isoformat()} is {age} days old; the catch-up bound "
            f"is {SNAPSHOT_MATURE_GRACE_DAYS} days. Re-predicting further back would "
            "store a hindsight forecast as if it had been made at the time. "
            "Retrospective backfill needs its own clearly-labelled backtest path.")


def run(snapshot_date: date | str | None = None, *, run_mature: bool = True,
        dry_run: bool = False, today: date | None = None) -> dict:
    """Run the nightly snapshot pass and (by default) the maturing pass.

    snapshot_date defaults to today and is the plant date every forecast is made for. It
    must be today or within SNAPSHOT_MATURE_GRACE_DAYS of it - see
    _validate_snapshot_date - and a violation raises SnapshotDateError, which is a
    rejected request (422), not a pass failure.

    dry_run computes and counts everything but issues no write, so a caller can see what
    a pass would do. today is injectable for tests; production always uses the real date -
    the maturing give-up line must never be evaluated against an arbitrary clock.

    Returns the PRD 4.2 summary dict, plus `snapshot.frozen`. Per-crop and per-row
    failures are counted in `errors` and never raise: this job is report-only and must
    not fail a pipeline.
    """
    if isinstance(snapshot_date, str):
        snapshot_date = date.fromisoformat(snapshot_date)
    snap_date = snapshot_date or date.today()
    as_of = today or date.today()
    _validate_snapshot_date(snap_date, as_of)

    engine = get_engine()

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
