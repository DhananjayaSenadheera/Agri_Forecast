"""Train Model A (harvest-price predictor) and register it.

Promotes only if it beats the carry-forward baseline on purged walk-forward CV.
"""
from agriforecast_ml.train.model import train_and_register


def main() -> None:
    print("=== Train Model A (pooled XGBoost harvest-price) ===")
    train_and_register(verbose=True)


if __name__ == "__main__":
    main()
