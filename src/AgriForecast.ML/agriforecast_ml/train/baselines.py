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
