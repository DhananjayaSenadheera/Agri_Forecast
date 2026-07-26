"""Sri Lankan Poya calendar helpers.

Poya stays a static Python table while the festival calendar lives in the DB: its
only consumer is QA gap suppression, which has to run with no DB, and it is not a
model feature.

Wraps agriforecast_ml.data.poya_days.POYA_DAYS with the two lookups
data_quality.gap_report needs so expected market-closed days are not reported as
data gaps.

Sunday is deliberately NOT treated as closed. The live HARTI corpus has 560 Sunday
rows against 2,657-2,716 per weekday, so suppressing every Sunday would hide real
gaps.
"""
from __future__ import annotations

from datetime import date

from .data.poya_days import POYA_DAYS

# Parsed once at import: is_poya() is called for every date in a gap scan.
_POYA_DATE_SET: frozenset[date] = frozenset(
    date.fromisoformat(d) for dates in POYA_DAYS.values() for d in dates
)


def is_poya(d: date) -> bool:
    """True if d is a full-moon Poya day per the static calendar.

    Dates outside the covered range (2015-2030) return False on purpose: an
    out-of-range date should surface as an ordinary gap and prompt someone to extend
    POYA_DAYS, not silently suppress one.
    """
    return d in _POYA_DATE_SET


def expected_market_closed(d: date) -> bool:
    """True if d is a day the HARTI daily bulletin is expected not to be published.

    Today that is exactly is_poya(d); Sunday is deliberately not included (see the
    module docstring). A future source with a different closed-day pattern should get
    its own predicate rather than adding branches here.
    """
    return is_poya(d)
