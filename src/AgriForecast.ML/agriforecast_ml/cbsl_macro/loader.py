"""CBSL macro loader: PublishedAt (vintage) resolution plus an idempotent upsert into
MacroSeriesPoints.

Vintage policy: first print only, and PublishedAt is the REAL publication date. Never
default PublishedAt to ReferenceDate - that is anti-conservative and leaks.

CCPI resolution order: the URL-embedded date (always available, since the downloader only
accepts links with a parseable filename date), cross-checked against the PDF
/CreationDate. If the two disagree, the LATER wins and we WARN - erring late only delays a
join, erring early leaks. Both are real signals, so IsPublishedAtImputed stays False.

MEI resolution order: the PDF /CreationDate (usually absent, but read defensively), then a
listing-page date if the caller has one (the scraper does not model one today), then
conservative-LATE imputation of ReferenceDate + a per-series lag prior with
IsPublishedAtImputed = True.

Idempotency: the upsert is keyed on the FULL unique triple (SeriesCode, ReferenceDate,
PublishedAt). A revised print of the same ReferenceDate with a NEW PublishedAt is a
distinct row by design; an exact key repeat, such as re-running the same artifact, updates
Value and RetrievedAtUtc in place.
"""
from __future__ import annotations

import logging
import re
import uuid
from datetime import date, datetime, timedelta, timezone
from typing import Sequence

import sqlalchemy as sa

from ..db import get_engine
from .parser import ParsedMacroPoint

logger = logging.getLogger(__name__)

# PDF /CreationDate format: "D:YYYYMMDDHHmmSS+HH'mm'" (harti/loader.py precedent).
_PDF_DATE_RE = re.compile(
    r"D:(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})"
    r"(?:([+-])(\d{2})'?(\d{2})'?|Z)?"
)


def _parse_pdf_creation_date(raw: "str | None") -> "datetime | None":
    """Parse a PDF /CreationDate string into a tz-aware UTC datetime.

    Mirrors harti/loader.py's version exactly, duplicated rather than imported so cbsl_macro/
    stays independently deletable from harti/.
    """
    if not raw:
        return None
    m = _PDF_DATE_RE.match(raw)
    if not m:
        return None
    y, mo, d, h, mi, s, sign, tzh, tzm = m.groups()
    try:
        naive = datetime(int(y), int(mo), int(d), int(h), int(mi), int(s))
    except ValueError:
        return None
    if sign is None:
        return naive.replace(tzinfo=timezone.utc)
    offset = timedelta(hours=int(tzh), minutes=int(tzm))
    if sign == "-":
        offset = -offset
    local = naive.replace(tzinfo=timezone(offset))
    return local.astimezone(timezone.utc)


# Conservative lag prior, used only when no real vintage signal exists at all, which for
# MEI is the common case. MEI packs describe data a month behind their own pack month and
# are confirmed on the site later still, so 45 days from the already-lagged ReferenceDate
# is deliberately generous: erring late only delays a join, erring early would leak.
_MEI_LAG_PRIOR_DAYS = 45


def _resolve_ccpi_published_at(
    filename_pub_date: "date | None",
    pdf_creation_date_raw: "str | None",
) -> tuple[date, bool]:
    """Resolve (PublishedAt, IsPublishedAtImputed) for a CCPI point.

    Both signals are real (never a lag-prior guess) -- IsPublishedAtImputed
    is always False here. If the URL date and the PDF's own /CreationDate
    disagree, the LATER of the two wins (conservative-late), and a WARNING
    is logged (spec requirement: "if they disagree use the LATER, WARN").
    """
    creation_dt = _parse_pdf_creation_date(pdf_creation_date_raw)
    creation_date = creation_dt.date() if creation_dt is not None else None

    if filename_pub_date is None and creation_date is None:
        raise ValueError("CCPI point has neither a filename date nor a parseable /CreationDate")
    if filename_pub_date is None:
        return creation_date, False
    if creation_date is None:
        return filename_pub_date, False

    if filename_pub_date != creation_date:
        later = max(filename_pub_date, creation_date)
        logger.warning(
            "CCPI PublishedAt sources disagree: URL date=%s, PDF /CreationDate=%s "
            "-- using the LATER (%s) per the conservative-late rule",
            filename_pub_date, creation_date, later,
        )
        return later, False

    return filename_pub_date, False


def _resolve_mei_published_at(
    reference_date: date,
    pdf_creation_date_raw: "str | None",
    listing_date: "date | None",
) -> tuple[date, bool]:
    """Resolve (PublishedAt, IsPublishedAtImputed) for an MEI point.

    Order: /CreationDate (real, probe-confirmed usually absent) -> listing
    date (real, if the caller has one) -> conservative-LATE lag-prior
    imputation (IsPublishedAtImputed=True). NEVER defaults to ReferenceDate.
    """
    creation_dt = _parse_pdf_creation_date(pdf_creation_date_raw)
    if creation_dt is not None:
        return creation_dt.date(), False
    if listing_date is not None:
        return listing_date, False

    imputed = reference_date + timedelta(days=_MEI_LAG_PRIOR_DAYS)
    logger.warning(
        "MEI point (ReferenceDate=%s) has no /CreationDate and no listing "
        "date -- imputing a conservative-LATE PublishedAt (%s, +%dd lag "
        "prior), IsPublishedAtImputed=True",
        reference_date, imputed, _MEI_LAG_PRIOR_DAYS,
    )
    return imputed, True


def resolve_published_at(point: ParsedMacroPoint, *, listing_date: "date | None" = None) -> tuple[date, bool]:
    """Dispatch to the per-source PublishedAt resolver."""
    if point.source == "CBSL_CCPI":
        return _resolve_ccpi_published_at(point.filename_pub_date, point.pdf_creation_date_raw)
    if point.source == "CBSL_MEI":
        return _resolve_mei_published_at(point.reference_date, point.pdf_creation_date_raw, listing_date)
    raise ValueError(f"Unknown CBSL macro source: {point.source!r}")


def upsert_macro_points(
    points: Sequence[ParsedMacroPoint],
    *,
    engine: sa.engine.Engine | None = None,
    dry_run: bool = False,
) -> dict:
    """Idempotent upsert into MacroSeriesPoints, keyed on the FULL vintage
    triple (SeriesCode, ReferenceDate, PublishedAt) -- matches the unique
    index IX_MacroSeriesPoints_SeriesCode_ReferenceDate_PublishedAt.

    A same-key repeat (identical SeriesCode/ReferenceDate/PublishedAt, e.g. a
    routine re-run over an unchanged artifact) UPDATEs Value/RetrievedAtUtc
    in place -- never duplicated. A genuinely revised print (same
    ReferenceDate, NEW PublishedAt) is a distinct row by construction of the
    key, per the vintage policy -- no splicing logic needed here.

    Returns counts: {"inserted", "updated", "skipped_invalid"}.
    """
    # Build the engine only when we actually need the DB, so a dry_run works in a hermetic
    # test or an environment with no DB configured.
    now_utc = datetime.now(timezone.utc)
    counters = dict(inserted=0, updated=0, skipped_invalid=0)

    to_upsert: list[dict] = []
    for p in points:
        try:
            published_at, is_imputed = resolve_published_at(p)
        except ValueError as exc:
            logger.warning("Skipping macro point %s/%s: %s", p.series_code, p.reference_date, exc)
            counters["skipped_invalid"] += 1
            continue

        if p.reference_date > published_at:
            logger.warning(
                "Skipping macro point %s/%s: resolved PublishedAt %s precedes "
                "ReferenceDate (would violate the ctor invariant) -- this "
                "should not happen given the resolvers above, treated as a "
                "hard data-quality skip",
                p.series_code, p.reference_date, published_at,
            )
            counters["skipped_invalid"] += 1
            continue

        to_upsert.append({
            "series_code": p.series_code,
            "reference_date": p.reference_date,
            "published_at": published_at,
            "value": p.value,
            "source": p.source,
            "is_published_at_imputed": is_imputed,
        })

    # Defensive in-batch dedup on the exact upsert key. The downloader already drops
    # duplicate-period artifacts, but two different artifacts could still resolve to the same
    # key, and that hits the unique index mid-transaction and aborts the ENTIRE batch (seen
    # live). Collapsing to the last occurrence keeps the pass alive; differing values for the
    # same key are logged loudly, since that means two values claim one vintage.
    dedup: dict[tuple[str, str, str], dict] = {}
    n_batch_dup = 0
    for row in to_upsert:
        key = (row["series_code"], row["reference_date"].isoformat(), row["published_at"].isoformat())
        if key in dedup:
            n_batch_dup += 1
            prior = dedup[key]
            if prior["value"] != row["value"]:
                logger.warning(
                    "In-batch duplicate key %s has CONFLICTING values (%s vs %s) "
                    "-- keeping the LAST one seen; investigate the source artifacts",
                    key, prior["value"], row["value"],
                )
            else:
                logger.info("In-batch duplicate key %s (identical value) -- collapsed to one row", key)
        dedup[key] = row
    to_upsert = list(dedup.values())
    if n_batch_dup:
        logger.warning(
            "Collapsed %d in-batch duplicate key(s) before upsert "
            "(should be rare -- the downloader layer already dedups known cases)",
            n_batch_dup,
        )

    logger.info(
        "Upsert candidates: %d of %d parsed points (skipped_invalid=%d, batch_dup_collapsed=%d)",
        len(to_upsert), len(points), counters["skipped_invalid"], n_batch_dup,
    )

    if dry_run:
        logger.info("DRY RUN — skipping DB writes")
        counters["inserted"] = len(to_upsert)
        return counters

    if not to_upsert:
        return counters

    if engine is None:
        engine = get_engine()

    with engine.begin() as conn:
        existing_keys: set[tuple[str, str, str]] = set()
        unique_series = list({r["series_code"] for r in to_upsert})
        if unique_series:
            placeholders = ", ".join(f":s{i}" for i in range(len(unique_series)))
            params = {f"s{i}": s for i, s in enumerate(unique_series)}
            rows = conn.execute(
                sa.text(
                    f"""SELECT SeriesCode,
                               CONVERT(varchar(10), ReferenceDate) as ReferenceDate,
                               CONVERT(varchar(10), PublishedAt) as PublishedAt
                        FROM MacroSeriesPoints
                        WHERE SeriesCode IN ({placeholders})"""
                ),
                params,
            ).fetchall()
            existing_keys = {(r[0], str(r[1]), str(r[2])) for r in rows}

        for row in to_upsert:
            key = (
                row["series_code"],
                row["reference_date"].isoformat(),
                row["published_at"].isoformat(),
            )
            if key in existing_keys:
                conn.execute(
                    sa.text(
                        """UPDATE MacroSeriesPoints
                           SET Value = :value,
                               Source = :source,
                               IsPublishedAtImputed = :is_published_at_imputed,
                               RetrievedAtUtc = :retrieved_at
                           WHERE SeriesCode = :series_code
                             AND ReferenceDate = :reference_date
                             AND PublishedAt = :published_at"""
                    ),
                    {
                        "value": row["value"],
                        "source": row["source"],
                        "is_published_at_imputed": row["is_published_at_imputed"],
                        "retrieved_at": now_utc,
                        "series_code": row["series_code"],
                        "reference_date": row["reference_date"],
                        "published_at": row["published_at"],
                    },
                )
                counters["updated"] += 1
            else:
                new_id = str(uuid.uuid4())
                conn.execute(
                    sa.text(
                        """INSERT INTO MacroSeriesPoints
                           (Id, SeriesCode, ReferenceDate, PublishedAt, Value,
                            Source, IsPublishedAtImputed, RetrievedAtUtc)
                           VALUES
                           (:id, :series_code, :reference_date, :published_at, :value,
                            :source, :is_published_at_imputed, :retrieved_at)"""
                    ),
                    {
                        "id": new_id,
                        "series_code": row["series_code"],
                        "reference_date": row["reference_date"],
                        "published_at": row["published_at"],
                        "value": row["value"],
                        "source": row["source"],
                        "is_published_at_imputed": row["is_published_at_imputed"],
                        "retrieved_at": now_utc,
                    },
                )
                counters["inserted"] += 1

    logger.info("MacroSeriesPoints upsert complete: inserted=%d, updated=%d, skipped_invalid=%d",
                counters["inserted"], counters["updated"], counters["skipped_invalid"])
    return counters
