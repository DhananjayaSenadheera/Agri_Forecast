"""Naive baselines Model A must beat to be worth shipping.

carry_forward: assume the harvest price equals today's price (the honest naive
predictor for "what will it be in N days"). crop_mean: the crop's average
harvest price seen in training.
"""
from __future__ import annotations

import numpy as np
import pandas as pd


def carry_forward_pred(eval_df: pd.DataFrame) -> np.ndarray:
    # current average price carried forward to harvest
    return eval_df["AvgPrice"].to_numpy(dtype=float)


def crop_mean_pred(train_df: pd.DataFrame, eval_df: pd.DataFrame) -> np.ndarray:
    means = train_df.groupby("CropId")["LabelHarvestPrice"].mean()
    overall = float(train_df["LabelHarvestPrice"].mean())
    return eval_df["CropId"].map(means).fillna(overall).to_numpy(dtype=float)


def crop_mean_map(train_df: pd.DataFrame):
    """Per-crop mean harvest price + global fallback, computed from TRAIN only.
    Returned so callers can reuse it as a residual offset without recomputation."""
    means = train_df.groupby("CropId")["LabelHarvestPrice"].mean()
    overall = float(train_df["LabelHarvestPrice"].mean())
    return means, overall


def offset_for(train_df: pd.DataFrame, eval_df: pd.DataFrame) -> np.ndarray:
    """Per-crop mean of `eval_df`'s crops, learned from `train_df`. Leakage-safe
    so long as `train_df` is strictly before `eval_df` in time."""
    means, overall = crop_mean_map(train_df)
    return eval_df["CropId"].map(means).fillna(overall).to_numpy(dtype=float)


def recency_weighted_crop_mean_pred(
    train_df: pd.DataFrame, eval_df: pd.DataFrame, halflife_days: float = 120.0
) -> np.ndarray:
    """Per-crop exponentially-recency-weighted mean harvest price.

    Weights each TRAIN label by 0.5 ** (age_days / halflife), where age is the
    distance of its ObservationDate from the latest TRAIN observation. Recent
    history dominates, so a crop whose price regime shifted up in the last few
    months is not dragged down by stale lows. Leakage-safe: uses TRAIN only.
    """
    t_ref = train_df["ObservationDate"].max()
    age = (t_ref - train_df["ObservationDate"]).dt.days.to_numpy(dtype=float)
    w = 0.5 ** (age / halflife_days)
    wy = w * train_df["LabelHarvestPrice"].to_numpy(dtype=float)
    g = pd.DataFrame({"CropId": train_df["CropId"].to_numpy(), "w": w, "wy": wy})
    agg = g.groupby("CropId").agg(sw=("w", "sum"), swy=("wy", "sum"))
    means = agg["swy"] / agg["sw"]
    overall = float(g["wy"].sum() / g["w"].sum())
    return eval_df["CropId"].map(means).fillna(overall).to_numpy(dtype=float)
