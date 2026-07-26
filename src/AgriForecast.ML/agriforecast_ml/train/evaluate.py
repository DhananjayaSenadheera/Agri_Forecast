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
    """Fraction of rows where the predicted price move matches the actual move.

    Direction is the sign of (price - reference), where reference is the price known AT
    the observation date - the row's own AvgPrice, the same vector carry-forward uses.
    Never pass the label or any harvest-time value as the reference; that would leak
    the answer into the metric.

    Rows with a NaN reference, prediction or actual are excluded and counted in
    n_excluded. A move no larger than deadband counts as flat, and flat against
    directional is a miss - hedging is not rewarded. With no scored rows the accuracy
    is None rather than 0.0, so it cannot be averaged as if the model were wrong.

    Returns {'directional_acc': float|None, 'n_scored': int, 'n_excluded': int}.
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
