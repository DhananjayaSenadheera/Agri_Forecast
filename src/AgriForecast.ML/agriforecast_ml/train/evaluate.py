"""Regression metrics."""
from __future__ import annotations

import numpy as np


def regression_metrics(y_true, y_pred) -> dict:
    y_true = np.asarray(y_true, dtype=float)
    y_pred = np.asarray(y_pred, dtype=float)
    err = y_pred - y_true
    mae = float(np.mean(np.abs(err)))
    rmse = float(np.sqrt(np.mean(err ** 2)))
    mape = float(np.mean(np.abs(err) / np.clip(np.abs(y_true), 1e-6, None)) * 100)
    return {"MAE": round(mae, 2), "RMSE": round(rmse, 2), "MAPE": round(mape, 2)}


def directional_accuracy(y_true, y_pred, reference, deadband: float = 0.0) -> dict:
    """Fraction of rows where the PREDICTED price move matches the ACTUAL move.

    Direction for a (crop, ObservationDate) row is the sign of
    (harvest_price - reference_price), where `reference` is the price KNOWN AT
    the observation date -- i.e. the row's own `AvgPrice` feature (see
    baselines.carry_forward_pred, which is exactly this reference). It is NEVER
    future-dated: using the label or any harvest-time value as the reference
    would leak the answer into the metric. Callers must therefore pass the same
    reference-price vector used by carry-forward.

    A "hit" is sign(pred - reference) == sign(actual - reference). This is the
    go/no-go signal that matters for the farmer recommendation: did we call the
    up/down direction right, regardless of the exact rupee error.

    Edge cases (all handled explicitly):
      * NaN reference (or NaN pred/actual): the row is EXCLUDED from the
        denominator. `n_excluded` reports how many rows were dropped so callers
        can see coverage. Accuracy is over the scored rows only.
      * Near-zero moves: a move whose magnitude is <= `deadband` (in price
        units, applied to BOTH the predicted and actual move) is treated as
        "flat" (sign 0). With the default deadband=0.0 this reduces to numpy's
        sign(0)==0, so an exactly-flat predicted move only counts as a hit when
        the actual move is also exactly flat. A flat-vs-directional pair is a
        miss. This is intentionally strict: we do not reward hedging.
      * No scored rows: accuracy is None (undefined, not 0.0) so it cannot be
        silently averaged as if the model were wrong.

    Returns {"directional_acc": float|None, "n_scored": int, "n_excluded": int}.
    """
    y_true = np.asarray(y_true, dtype=float)
    y_pred = np.asarray(y_pred, dtype=float)
    reference = np.asarray(reference, dtype=float)

    valid = ~(np.isnan(y_true) | np.isnan(y_pred) | np.isnan(reference))
    n_excluded = int((~valid).sum())

    actual_move = y_true[valid] - reference[valid]
    pred_move = y_pred[valid] - reference[valid]

    def _sign(move):
        s = np.sign(move)
        if deadband > 0:
            s = np.where(np.abs(move) <= deadband, 0.0, s)
        return s

    n_scored = int(valid.sum())
    if n_scored == 0:
        acc = None
    else:
        hits = _sign(pred_move) == _sign(actual_move)
        acc = round(float(np.mean(hits)), 4)
    return {"directional_acc": acc, "n_scored": n_scored, "n_excluded": n_excluded}
