"""CLI: one-time backfill of extended HARTI-Pettah fruit history into MarketPrices
for the two owner-adopted crops (FRT000003 Ambul, FRT000006 Seeni).

Run (from src/AgriForecast.ML):
  set -a && . ./.env && set +a
  ./.venv/bin/python backfill_fruit_history.py --dry-run   # inspect, no writes
  ./.venv/bin/python backfill_fruit_history.py             # apply (post-merge only)

Idempotent: re-running after apply inserts 0.  After a real apply it runs the
MarketPrices splice invariant (harti.qa.assert_no_source_duplicates) as a guard.
"""
from __future__ import annotations

import argparse
import logging

from agriforecast_ml.envfile import load_env_file
from agriforecast_ml.db import get_engine
from agriforecast_ml.harti import fruit_history_backfill as fhb
from agriforecast_ml.harti import qa


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--dry-run", action="store_true",
                    help="resolve + report counts but write nothing")
    args = ap.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    load_env_file()
    engine = get_engine()

    rows = fhb.build_fruit_history_rows(engine)
    print(f"Built {len(rows)} rows across {rows['CropCode'].nunique()} crops:")
    for code, g in rows.groupby("CropCode"):
        print(f"  {code} {g['CropName'].iloc[0]:16s} {len(g):5d} rows  "
              f"{g['PriceDate'].min().date()} -> {g['PriceDate'].max().date()}  "
              f"AvgPrice median={g['AvgPrice'].median():.2f}  ext_id={int(g['ExternalProductId'].iloc[0])}")

    counters = fhb.backfill_fruit_history(engine, dry_run=args.dry_run)
    print(f"\nCounters: {counters}")

    if not args.dry_run:
        n = qa.assert_no_source_duplicates(engine)
        print(f"Splice invariant OK: {n} HARTI MarketPrices rows, 0 DEC overlaps.")


if __name__ == "__main__":
    main()
