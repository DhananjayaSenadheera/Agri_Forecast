"""Sri Lankan Poya calendar helpers (R1.1 P1 Step 5, ClickUp 86cahef64).

NOTE (deliberate asymmetry with festivals): Poya stays Python-static here while
the festival calendar lives in the DB (load.load_festivals). Reason: Poya's only
consumer is QA gap-suppression (must run with no DB, not a model feature);
festivals ARE model features that as-of-join price history, so the .NET seed owns
their lifecycle. See load_festivals()'s docstring for the mirror of this note.

Wraps the static ``agriforecast_ml.data.poya_days.POYA_DAYS`` table (see that
module's docstring for provenance/confidence notes — dates need one-time
human verification against an official source) with the two lookups
``data_quality.gap_report`` needs to avoid misreporting expected market-
closed days as data gaps.

SUNDAY HANDLING — this was decided from real corpus evidence, not assumed:
  A live query against MarketPrices (Source='HARTI', the real 11-year HARTI
  corpus) shows HARTI publishes on Sundays too, just far less often than
  weekdays (560 Sunday rows / 2,657-2,716 per weekday, ~7,800 total dates —
  roughly one Sunday bulletin every ~2-3 weeks, not zero). Saturday is
  similar (542 rows). So Sunday is NOT an expected-closed day for this
  corpus — treating every Sunday as "expected closed" would suppress real
  gap signal on the ~20% of Sundays HARTI actually did publish.

  Only Poya suppresses (``expected_market_closed`` = ``is_poya`` today).
  Sunday is intentionally NOT OR'd in, contradicting the task brief's
  starting assumption ("Poya OR Sunday") — the brief itself said to verify
  against the live corpus before assuming, and the verification result was
  negative for blanket Sunday suppression. See data_quality.py's gap_report
  docstring for the numbers this was based on.
"""
from __future__ import annotations

from datetime import date

from .data.poya_days import POYA_DAYS

# Pre-computed as a set of date objects once at import time (small: ~200
# entries across 2015-2030) — is_poya() is called once per date in a gap
# scan, so this avoids re-parsing ISO strings on every call.
_POYA_DATE_SET: frozenset[date] = frozenset(
    date.fromisoformat(d) for dates in POYA_DAYS.values() for d in dates
)


def is_poya(d: date) -> bool:
    """True if ``d`` is a Sri Lankan full-moon Poya day per the static
    calendar (agriforecast_ml/data/poya_days.py).

    Returns False for any date outside the covered range (2015-2030) —
    this is a deliberate fail-open-to-"not Poya" choice: a gap check on an
    out-of-range date should not silently suppress a real gap just because
    the calendar wasn't extended that far; it should surface as an ordinary
    gap and prompt someone to extend POYA_DAYS.
    """
    return d in _POYA_DATE_SET


def expected_market_closed(d: date) -> bool:
    """True if ``d`` is a day the HARTI daily bulletin is expected NOT to be
    published, for reasons that are not a data-pipeline problem.

    Today this is exactly ``is_poya(d)`` — Sunday is deliberately NOT
    included (see module docstring: live corpus evidence shows HARTI does
    publish on a meaningful fraction of Sundays, so treating all Sundays as
    expected-closed would suppress real gap signal). If a future source
    (CBSL, DEC) has a genuinely different closed-day pattern, that source
    should get its own predicate rather than this function growing
    per-source branches.
    """
    return is_poya(d)
