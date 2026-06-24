"""FastAPI service exposing harvest-price predictions.

Internal service (called by the .NET API). No auth in this MVP — add an API
key / network isolation before any external exposure.
"""
from __future__ import annotations

from datetime import date

from fastapi import FastAPI
from pydantic import BaseModel

from . import predict

app = FastAPI(title="AgriForecast ML — Model A", version="1.0")


class PredictRequest(BaseModel):
    cropId: str
    plantDate: date


@app.get("/health")
def health():
    return {"status": "ok"}


@app.get("/model-info")
def model_info():
    return predict.model_info()


@app.post("/predict")
def predict_endpoint(req: PredictRequest):
    return predict.predict_harvest(req.cropId, req.plantDate)
