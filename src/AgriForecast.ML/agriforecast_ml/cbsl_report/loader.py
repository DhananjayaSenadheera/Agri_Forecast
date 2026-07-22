"""CBSL Daily Price Report -> PriceObservations loader (capture-only v1).

Follows harti/loader.upsert_harti_price_observations' contracts exactly, with
one deliberate inversion:

PRICE COLUMNS: CBSL publishes a SINGLE POINT figure per (market, item, day) —
the exact case PriceObservations' WholesalePrice/RetailPrice columns exist for.
CBSL rows therefore populate WholesalePrice (wholesale markets) or RetailPrice
(retail markets) and ALWAYS leave MinPrice/MaxPrice NULL — the inverse of
HARTI's min/max convention. Consequences, both deliberate:
  * data_quality.flag_price_outliers scans MinPrice/MaxPrice midpoints, so
    CBSL rows are outside its scope by design (capture-only: no CBSL row can
    reach the feature layer yet anyway).
  * A future feature layer must read WholesalePrice/RetailPrice for
    Source='CBSL', never Min/Max (which are NULL here).

MARKET RESOLUTION (BY NAME, never a GUID): the wholesale Pettah column is the
same physical market HARTI's Pettah bulletin covers (Manning Market), and CBSL
Narahenpita retail is HARTI's Narahenpita retail fair — so those two resolve to
the EXISTING "(HARTI ...)"-suffixed Markets rows (the suffix is a display-name
legacy, not a source scope; the PriceObservations Source column is the scope).
Dambulla wholesale resolves to the first-class Dambulla DEC row — a deliberate
third source on that market for cross-validation (adjudicated in
data_quality._ADJUDICATED_OVERLAPS). The two RETAIL columns for Pettah/Dambulla
have NO Markets rows yet — they carry placeholder names and WARN-skip every row
until a future migration seeds them (HARTI's unseeded-market precedent).
NOTE (collision guard): each SEEDED market maps from exactly ONE CBSL column
today. If the retail placeholders are ever pointed at the SAME rows as the
wholesale columns, the (Market, Name, Date, Source) upsert key would collide —
seed distinct retail rows instead, or merge to one row carrying both columns.

IDEMPOTENCY: same filtered unique index as HARTI —
UX_PriceObservations_MarketCommodityNameDateSource on (MarketId,
ExternalCommodityName, ObservedDate, Source), ExternalCommodityId NULL.

CROP RESOLUTION: CommodityAliasResolver scoped Source='CBSL' with global
fallback; unresolved -> CropId NULL + WARN, healed later — never guessed.

POINT-IN-TIME: AsOfUtc = the report PDF's own /CreationDate (publication
vintage), with harti.loader._resolve_as_of_utc's conservative-LATE fallback —
reused directly so the two PDF sources share one vintage-resolution truth.

UNITS: parser emits Rs./kg rows ONLY (v1 scope filter), a verified constant of
the probed corpus -> UnitRaw="Rs./kg", factor 1.0, IsUnitConfirmed=1 (except a
validate_price_row hold), per canonical.py's fail-closed unit contract.
"""
from __future__ import annotations

import logging
import uuid
from datetime import date, datetime, timezone
from typing import Sequence

import sqlalchemy as sa

from ..canonical import CommodityAliasResolver
from ..data_quality import validate_price_row
from ..db import get_engine
# Shared vintage resolution + GUID normalisation (single source of truth for
# PDF-published sources — see harti/loader.py for the full leakage rationale).
from ..harti.loader import _parse_guid, _resolve_as_of_utc
from .parser import ParsedCbslPrice

logger = logging.getLogger(__name__)

SOURCE = "CBSL"

CBSL_UNIT_RAW = "Rs./kg"
CBSL_UNIT_CONVERSION_FACTOR = 1.0

# parser market_name -> DB Markets.Name (resolved BY NAME at runtime).
# The two retail placeholders WARN-skip until their rows are seeded.
_CBSL_MARKET_TO_DB_NAME: dict[str, str] = {
    "Pettah (wholesale)":    "Pettah (HARTI wholesale)",
    "Dambulla (wholesale)":  "Dambulla Dedicated Economic Centre",
    "Narahenpita (retail)":  "Narahenpita (HARTI retail)",
    # Placeholders — no Markets rows yet (see module docstring):
    "Pettah (retail)":       "Pettah (CBSL retail)",
    "Dambulla (retail)":     "Dambulla (CBSL retail)",
}

# Which PriceObservations column a market column's point figure belongs in.
_RETAIL_MARKET_KEYS = {"Pettah (retail)", "Dambulla (retail)", "Narahenpita (retail)"}


def _build_market_map(engine: sa.engine.Engine) -> dict[str, uuid.UUID]:
    """{parser_market_name: MarketId} resolved BY NAME (harti/loader idiom)."""
    db_names = list(_CBSL_MARKET_TO_DB_NAME.values())
    placeholders = ", ".join(f":n{i}" for i in range(len(db_names)))
    params = {f"n{i}": n for i, n in enumerate(db_names)}
    with engine.connect() as conn:
        rows = conn.execute(
            sa.text(f"SELECT Id, Name FROM Markets WHERE Name IN ({placeholders})"),
            params,
        ).fetchall()
    db_name_to_id = {row[1]: _parse_guid(row[0]) for row in rows}

    result: dict[str, uuid.UUID] = {}
    for parser_name, db_name in _CBSL_MARKET_TO_DB_NAME.items():
        if db_name not in db_name_to_id:
            logger.warning(
                "DB market not found for CBSL column %r (expected DB name %r) — "
                "rows for this column will be skipped, not invented",
                parser_name, db_name,
            )
            continue
        result[parser_name] = db_name_to_id[db_name]
    logger.info("CBSL market map built: %d of %d columns resolved",
                len(result), len(_CBSL_MARKET_TO_DB_NAME))
    return result


def upsert_cbsl_price_observations(
    parsed_rows: Sequence[ParsedCbslPrice],
    *,
    engine: sa.engine.Engine | None = None,
    dry_run: bool = False,
) -> dict:
    """Upsert parsed CBSL TODAY prices into PriceObservations.

    Returns counts: inserted, updated, skipped_no_market,
    skipped_invalid_price, crop_resolved, crop_unresolved.
    """
    if engine is None:
        engine = get_engine()

    market_map = _build_market_map(engine)          # once per call
    crop_resolver = CommodityAliasResolver(engine)  # once per call
    now_utc = datetime.now(timezone.utc)

    counters = dict(
        inserted=0, updated=0,
        skipped_no_market=0, skipped_invalid_price=0,
        crop_resolved=0, crop_unresolved=0,
    )

    to_upsert: list[dict] = []
    for pr in parsed_rows:
        if pr.market_name not in market_map:
            logger.warning(
                "[%s] %s: CBSL column %r has no DB Market — skipping row, "
                "not inventing a market",
                pr.date_str, pr.cbsl_label, pr.market_name,
            )
            counters["skipped_no_market"] += 1
            continue

        observed_date = date.fromisoformat(pr.date_str)

        # Shared validator: a single point figure is validated as a degenerate
        # min==max range — the non-positive-price REJECT branch is the one that
        # can fire (min>max is impossible here by construction).
        validation = validate_price_row(
            min_price=pr.price, max_price=pr.price,
            source=SOURCE, crop_label=pr.cbsl_label,
            observed_date=observed_date, market_name=pr.market_name,
        )
        if validation.reject:
            logger.warning(validation.message)
            counters["skipped_invalid_price"] += 1
            continue
        if validation.hold:
            logger.warning(validation.message)

        crop_id = crop_resolver.resolve(pr.cbsl_label, SOURCE)
        if crop_id is None:
            logger.warning(
                "[%s] %s/%s: no active CommodityAlias resolved this label — "
                "CropId left NULL, never guessed",
                pr.date_str, pr.market_name, pr.cbsl_label,
            )
            counters["crop_unresolved"] += 1
        else:
            counters["crop_resolved"] += 1

        is_retail = pr.market_name in _RETAIL_MARKET_KEYS
        to_upsert.append({
            "market_id": str(market_map[pr.market_name]),
            "crop_id": str(crop_id) if crop_id is not None else None,
            "external_commodity_name": pr.cbsl_label,
            "observed_date": observed_date,
            "wholesale_price": None if is_retail else pr.price,
            "retail_price": pr.price if is_retail else None,
            "as_of_utc": _resolve_as_of_utc(pr.pdf_creation_date_raw, observed_date),
            "unit_raw": CBSL_UNIT_RAW,
            "unit_conversion_factor": CBSL_UNIT_CONVERSION_FACTOR,
            "is_unit_confirmed": not validation.hold,
        })

    logger.info(
        "CBSL PriceObservations upsert candidates: %d rows (of %d parsed). "
        "no-market=%d, invalid-price=%d, crop-resolved=%d, crop-unresolved=%d",
        len(to_upsert), len(parsed_rows),
        counters["skipped_no_market"], counters["skipped_invalid_price"],
        counters["crop_resolved"], counters["crop_unresolved"],
    )

    if dry_run:
        logger.info("DRY RUN — skipping DB writes")
        counters["inserted"] = len(to_upsert)  # report as-if
        return counters
    if not to_upsert:
        return counters

    with engine.begin() as conn:
        existing_keys: set[tuple[str, str, str]] = set()
        unique_market_ids = list({r["market_id"] for r in to_upsert})
        if unique_market_ids:
            placeholders = ", ".join(f":mid{i}" for i in range(len(unique_market_ids)))
            params_mids = {f"mid{i}": mid for i, mid in enumerate(unique_market_ids)}
            existing_rows = conn.execute(
                sa.text(
                    f"""SELECT CONVERT(varchar(36), MarketId) as MarketId,
                               ExternalCommodityName,
                               CONVERT(varchar(10), ObservedDate) as ObservedDate
                        FROM PriceObservations
                        WHERE Source = :src
                          AND ExternalCommodityId IS NULL
                          AND MarketId IN ({placeholders})"""
                ),
                {"src": SOURCE, **params_mids},
            ).fetchall()
            existing_keys = {
                (str(r[0]).upper(), str(r[1]), str(r[2])) for r in existing_rows
            }

        for row in to_upsert:
            key = (
                row["market_id"].upper(),
                row["external_commodity_name"],
                row["observed_date"].isoformat(),
            )
            if key in existing_keys:
                # CropId COALESCE (NULL -> resolved only) and the IsUnitConfirmed
                # sticky-down ratchet mirror harti/loader — see its UPDATE-branch
                # comment for the full quarantine rationale.
                conn.execute(
                    sa.text(
                        """UPDATE PriceObservations
                           SET WholesalePrice = :wholesale_price,
                               RetailPrice = :retail_price,
                               AsOfUtc = :as_of_utc,
                               RetrievedAtUtc = :retrieved_at,
                               CropId = COALESCE(CropId, :crop_id),
                               UnitRaw = :unit_raw,
                               UnitConversionFactor = :unit_conversion_factor,
                               IsUnitConfirmed = CASE
                                   WHEN IsUnitConfirmed = 0 THEN 0
                                   ELSE :is_unit_confirmed
                               END
                           WHERE CONVERT(varchar(36), MarketId) = :market_id
                             AND ExternalCommodityName = :ext_commodity_name
                             AND ExternalCommodityId IS NULL
                             AND ObservedDate = :observed_date
                             AND Source = :source"""
                    ),
                    {
                        "wholesale_price": row["wholesale_price"],
                        "retail_price": row["retail_price"],
                        "as_of_utc": row["as_of_utc"],
                        "retrieved_at": now_utc,
                        "crop_id": row["crop_id"],
                        "unit_raw": row["unit_raw"],
                        "unit_conversion_factor": row["unit_conversion_factor"],
                        "is_unit_confirmed": row["is_unit_confirmed"],
                        "market_id": row["market_id"],
                        "ext_commodity_name": row["external_commodity_name"],
                        "observed_date": row["observed_date"],
                        "source": SOURCE,
                    },
                )
                counters["updated"] += 1
            else:
                conn.execute(
                    sa.text(
                        """INSERT INTO PriceObservations
                           (Id, MarketId, CropId, ExternalCommodityId,
                            ExternalCommodityName, ObservedDate,
                            WholesalePrice, RetailPrice, MinPrice, MaxPrice,
                            ArrivalsKg, AsOfUtc, Source, RetrievedAtUtc,
                            UnitRaw, UnitConversionFactor, IsUnitConfirmed)
                           VALUES
                           (:id, :market_id, :crop_id, NULL,
                            :ext_commodity_name, :observed_date,
                            :wholesale_price, :retail_price, NULL, NULL,
                            NULL, :as_of_utc, :source, :retrieved_at,
                            :unit_raw, :unit_conversion_factor, :is_unit_confirmed)"""
                    ),
                    {
                        "id": str(uuid.uuid4()),
                        "market_id": row["market_id"],
                        "crop_id": row["crop_id"],
                        "ext_commodity_name": row["external_commodity_name"],
                        "observed_date": row["observed_date"],
                        "wholesale_price": row["wholesale_price"],
                        "retail_price": row["retail_price"],
                        "as_of_utc": row["as_of_utc"],
                        "source": SOURCE,
                        "retrieved_at": now_utc,
                        "unit_raw": row["unit_raw"],
                        "unit_conversion_factor": row["unit_conversion_factor"],
                        "is_unit_confirmed": row["is_unit_confirmed"],
                    },
                )
                counters["inserted"] += 1

    logger.info("CBSL PriceObservations DB upsert complete: inserted=%d, updated=%d",
                counters["inserted"], counters["updated"])
    return counters
