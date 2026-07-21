"""CLI: mirror MarketPrices(Source='DAMBULLA_DEC') into PriceObservations
(R2 Step 2, ClickUp 86cajtx2k). See agriforecast_ml/dec_mirror.py for the full
design rationale (set-based, insert-only, id-keyed identity, AsOfUtc choice).

One-time backfill (run once, from src/AgriForecast.ML):
  set -a && . ./.env && set +a
  ./.venv/bin/python mirror_dec_prices.py --dry-run   # inspect, no writes
  ./.venv/bin/python mirror_dec_prices.py              # apply

Daily incremental (wired into run-daily.sh, AFTER market ingest / BEFORE
build_features -- see that script):
  ./.venv/bin/python mirror_dec_prices.py --since-date <last-watermark>

Idempotent either way: re-running with nothing new to mirror reports
inserted=0. Exits non-zero on failure (e.g. the Dambulla Markets row is
missing) so a daily-pipeline caller sees the failure.
"""
from __future__ import annotations

import argparse
import logging
import sys
from datetime import date

from agriforecast_ml.envfile import load_env_file
from agriforecast_ml.db import get_engine
from agriforecast_ml import dec_mirror


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dry-run", action="store_true",
                    help="resolve + report counts but write nothing")
    ap.add_argument("--since-date", type=str, default=None,
                    help="ISO YYYY-MM-DD; only mirror MarketPrices rows with "
                         "PriceDate strictly after this date (daily-delta "
                         "optimisation -- correctness does not depend on it, "
                         "the anti-join is idempotent with or without it)")
    args = ap.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    since_date = date.fromisoformat(args.since_date) if args.since_date else None

    load_env_file()
    engine = get_engine()

    try:
        result = dec_mirror.mirror_dec_to_price_observations(
            engine, since_date=since_date, dry_run=args.dry_run,
        )
    except Exception:
        logging.exception("mirror_dec_prices: FAILED")
        return 1

    mode = "DRY RUN" if args.dry_run else "APPLY"
    print(f"=== DEC -> PriceObservations mirror ({mode}) ===")
    print(f"  since_date:            {since_date.isoformat() if since_date else '(none — full scan)'}")
    print(f"  market_id:             {result['market_id']}")
    print(f"  eligible_total:        {result['eligible_total']}")
    print(f"  already_mirrored:      {result['already_mirrored']}")
    print(f"  would_insert:          {result['would_insert']}")
    print(f"  inserted:              {result['inserted']}")
    print(f"  skipped_non_positive:  {result['skipped_non_positive']}")
    print(f"  skipped_null_crop:     {result['skipped_null_crop']}")
    print(f"  skipped_null_extid:    {result['skipped_null_extid']}")
    print(f"  distinct_crops:        {result['distinct_crops']}")
    print(f"  date_range:            {result['min_observed_date']} -> {result['max_observed_date']}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
