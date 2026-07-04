"""CBSL macro loader — PublishedAt (vintage) resolution + idempotent upsert
into MacroSeriesPoints.

VINTAGE POLICY (2026-07-04 P3 decision, DECISIONS.md — this is the spec):
  First-print only; KnowledgeDate (PublishedAt) = the real publication date.
  NEVER default PublishedAt := ReferenceDate (anti-conservative / lookahead).
  Resolution order, per source:

    CCPI (source="CBSL_CCPI"):
      1. URL-embedded date (press_YYYYMMDD_...) -- always available (the
         downloader only accepts links with a parseable filename date).
      2. PDF /CreationDate -- probe-confirmed present AND agreeing with the
         URL date on all 3 real fixtures. Cross-checked against (1): if they
         DISAGREE, use the LATER of the two (never the earlier -- erring
         late only delays a join, erring early leaks) and WARN.
      IsPublishedAtImputed = False in both cases (both are real, recovered
      publication signals, never a lag-prior guess).

    MEI (source="CBSL_MEI"):
      1. PDF /CreationDate -- probe-confirmed ABSENT on the MEI_202605
         fixture (metadata carries only ModDate, no CreationDate key). Read
         defensively in case some MEI pack someday carries one.
      2. Listing-page date, if the caller supplies one (not modelled by this
         MVP's scraper -- the MEI listing page probed 2026-07-04 shows no
         per-item published date, only the pack's own YYYYMM in the
         filename) -- kept as a plumbed-through parameter for forward
         compatibility, currently always None in practice.
      3. Conservative-LATE imputation: ReferenceDate + a per-series lag
         prior (see _MEI_LAG_PRIOR_DAYS), IsPublishedAtImputed = True.
         The lag prior is deliberately generous (mirrors HARTI's
         conservative-late reasoning in harti/loader.py's
         _resolve_as_of_utc): MEI packs are observed 1 calendar month behind
         their own pack month for trade data and published only once the
         FOLLOWING pack's page confirms them, so both FOOD_IMPORTS_YOY and
         POLICY_RATE_OPR use the PACK'S OWN month + a full-month-plus-buffer
         lag from ReferenceDate as the imputed vintage.

Idempotency: upsert keyed on the FULL unique triple (SeriesCode,
ReferenceDate, PublishedAt) -- matches
IX_MacroSeriesPoints_SeriesCode_ReferenceDate_PublishedAt (unique). A revised
print of the same ReferenceDate with a NEW PublishedAt is a distinct row by
design (never skipped as "already present") -- this loader never revises an
existing (SeriesCode, ReferenceDate, PublishedAt) row's Value; it only
inserts a first-print + updates RetrievedAtUtc/Value on an EXACT key repeat
(e.g. a re-run of the same artifact).
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

    Mirrors harti/loader.py's ``_parse_pdf_creation_date`` exactly (same
    regex, same optional-offset handling) -- duplicated rather than imported
    to keep cbsl_macro/ independently deletable from harti/.
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


# Per-series conservative lag prior applied when NO real vintage signal is
# available at all (used for MEI when both /CreationDate and a listing date
# are absent, which per the 2026-07-04 probe is the common case for MEI).
# MEI packs describe DATA one month behind their own pack month and are only
# confirmed on cbsl.gov.lk sometime after the following month begins; 45 days
# from the (already 1-month-lagged) ReferenceDate is a deliberately generous,
# conservative-LATE buffer -- erring late only delays a downstream join,
# erring early would leak.
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
    # Engine construction is deferred until we know we actually need to talk
    # to the DB (i.e. NOT dry_run) -- a dry_run call must be usable in a
    # hermetic test / no-DB-configured environment without raising on engine
    # construction alone.
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

    # Defensive in-batch dedup on the exact upsert key (SeriesCode,
    # ReferenceDate, PublishedAt). Belt-and-suspenders: the downloader layer
    # already dedups duplicate-period artifacts before they ever reach the
    # parser (see cbsl_macro.downloader._dedup_by_key — a real Drupal
    # duplicate-upload was caught this way during live verification), but two
    # DIFFERENT artifacts (e.g. a CCPI release AND a stale cached copy) could
    # in principle resolve to the identical key. Without this, a genuine
    # in-batch duplicate hits the DB's unique index mid-transaction and
    # aborts the ENTIRE batch (observed live) -- collapsing to the LAST
    # occurrence here (values should be identical for a true duplicate;
    # logged loudly if they are not, since that would mean two DIFFERENT
    # values are claiming the same vintage) keeps the pass from failing
    # loudly bombing wholesale over one bad pair.
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
