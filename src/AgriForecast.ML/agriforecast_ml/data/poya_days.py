"""Sri Lankan full-moon Poya day calendar, 2015-2030.

WHY THIS EXISTS (ClickUp 86cahef64, R1.1 P1 Step 5): Poya days are
mercantile-holiday, market-closed days in Sri Lanka (banks, government
offices, and — per corpus behaviour verified against the live HARTI
PriceObservations/MarketPrices corpus, see data_quality.py module docstring
— HARTI's daily wholesale bulletin is also not published). A gap-detection
routine that does not know about Poya days would misreport every single
Poya as a 1-day "missing data" gap, drowning the real signal (actual missing
PDFs, download failures, market disruptions) in ~12-13 false positives a
year, every year. This file is the single source of truth so that
suppression rule lives in exactly one place.

============================================================================
PROVENANCE / CONFIDENCE — READ BEFORE TRUSTING THESE DATES IN PRODUCTION
============================================================================
These dates were populated from the assistant's general knowledge of the
Sri Lankan full-moon Poya calendar (which is itself derived from the actual
astronomical full moon in Sri Lanka's timezone, published yearly by the
Sri Lankan government / Department of Buddhist Affairs). They are NOT
computed astronomically at runtime (deliberate design choice — see
data_quality.py: a static, human-auditable list is preferred over a runtime
ephemeris dependency for a fact that only changes once a year, in a known,
finite way).

CONFIDENCE BY YEAR RANGE (self-assessed, be skeptical of all of it):
  - 2015-2026 (inclusive): HIGH confidence. These are actual historical
    dates (2015-2025 are fully in the past as of this task's "today" of
    2026-07-02; 2026 is mostly in the past/current). Cross-checked
    internally for correct ~29.5-day lunar spacing and no duplicates (see
    tests/test_poya.py structural tests), but NOT independently verified
    against an official Sri Lankan government gazette or bank-holiday
    circular by a human. Treat as "very likely correct, needs one human
    spot-check before this is trusted for anything financially binding."
  - 2027-2030: LOWER confidence. These are future dates computed by the
    same lunar-cycle reasoning but further from any date the assistant has
    strong grounding on, and Sri Lanka's official calendar occasionally
    shifts a Poya day by ±1 calendar date late (due to exact moonrise
    timing / regional calendar authority decisions) versus a naive
    astronomical calculation. These entries should be treated as
    PROVISIONAL and re-verified against an official source (e.g.
    Department of Buddhist Affairs / bank holiday circular) roughly a year
    ahead of each date actually being used operationally.

ACTION REQUIRED before this file is relied on for anything beyond
suppressing gap-report false positives in this task: a human must verify
every date below (2015-2030) against an official Sri Lankan source (e.g.
https://www.cbsl.gov.lk bank holiday circulars, or the Department of
Buddhist Affairs gazette) and remove this notice once done. Until then,
gap_report()'s Poya suppression is "best effort," not authoritative.

============================================================================
STRUCTURE
============================================================================
``POYA_DAYS`` maps year -> sorted list of date.isoformat() strings ("YYYY-
MM-DD"). Most years have 12 Poya days (one per lunar month); years
containing an intercalary ("Adhi"/leap) lunar month have 13. Both counts are
valid and asserted in the structural test (12 or 13 per year, never fewer,
never more) rather than hardcoding 12 for every year.
"""
from __future__ import annotations

POYA_DAYS: dict[int, list[str]] = {
    2015: [
        "2015-01-04", "2015-02-03", "2015-03-05", "2015-04-04",
        "2015-05-03", "2015-06-02", "2015-07-01", "2015-07-31",
        "2015-08-29", "2015-09-27", "2015-10-27", "2015-11-25",
        "2015-12-25",
    ],
    2016: [
        "2016-01-23", "2016-02-22", "2016-03-22", "2016-04-21",
        "2016-05-21", "2016-06-19", "2016-07-19", "2016-08-17",
        "2016-09-16", "2016-10-15", "2016-11-13", "2016-12-13",
    ],
    2017: [
        "2017-01-11", "2017-02-10", "2017-03-11", "2017-04-10",
        "2017-05-10", "2017-06-08", "2017-07-08", "2017-08-06",
        "2017-09-05", "2017-10-04", "2017-11-03", "2017-12-03",
    ],
    2018: [
        "2018-01-01", "2018-01-31", "2018-03-01", "2018-03-30",
        "2018-04-29", "2018-05-29", "2018-06-27", "2018-07-27",
        "2018-08-25", "2018-09-24", "2018-10-24", "2018-11-22",
        "2018-12-22",
    ],
    2019: [
        "2019-01-20", "2019-02-19", "2019-03-20", "2019-04-19",
        "2019-05-18", "2019-06-16", "2019-07-16", "2019-08-14",
        "2019-09-13", "2019-10-13", "2019-11-11", "2019-12-11",
    ],
    2020: [
        "2020-01-10", "2020-02-08", "2020-03-09", "2020-04-07",
        "2020-05-07", "2020-06-05", "2020-07-04", "2020-08-03",
        "2020-09-01", "2020-10-01", "2020-10-30", "2020-11-29",
        "2020-12-29",
    ],
    2021: [
        "2021-01-28", "2021-02-26", "2021-03-27", "2021-04-26",
        "2021-05-25", "2021-06-24", "2021-07-23", "2021-08-21",
        "2021-09-20", "2021-10-19", "2021-11-18", "2021-12-18",
    ],
    2022: [
        "2022-01-17", "2022-02-15", "2022-03-16", "2022-04-15",
        "2022-05-15", "2022-06-13", "2022-07-13", "2022-08-11",
        "2022-09-09", "2022-10-09", "2022-11-07", "2022-12-07",
    ],
    2023: [
        "2023-01-06", "2023-02-04", "2023-03-06", "2023-04-05",
        "2023-05-04", "2023-06-03", "2023-07-02", "2023-08-01",
        "2023-08-30", "2023-09-29", "2023-10-28", "2023-11-26",
        "2023-12-26",
    ],
    2024: [
        "2024-01-25", "2024-02-23", "2024-03-24", "2024-04-23",
        "2024-05-23", "2024-06-21", "2024-07-20", "2024-08-19",
        "2024-09-17", "2024-10-17", "2024-11-15", "2024-12-15",
    ],
    2025: [
        "2025-01-13", "2025-02-12", "2025-03-13", "2025-04-12",
        "2025-05-12", "2025-06-10", "2025-07-10", "2025-08-08",
        "2025-09-07", "2025-10-06", "2025-11-05", "2025-12-04",
    ],
    2026: [
        "2026-01-03", "2026-02-01", "2026-03-03", "2026-04-01",
        "2026-05-01", "2026-05-31", "2026-06-29", "2026-07-29",
        "2026-08-27", "2026-09-26", "2026-10-25", "2026-11-24",
        "2026-12-23",
    ],
    2027: [
        "2027-01-22", "2027-02-20", "2027-03-22", "2027-04-21",
        "2027-05-20", "2027-06-19", "2027-07-18", "2027-08-17",
        "2027-09-15", "2027-10-15", "2027-11-14", "2027-12-13",
    ],
    2028: [
        "2028-01-12", "2028-02-10", "2028-03-10", "2028-04-09",
        "2028-05-08", "2028-06-06", "2028-07-06", "2028-08-04",
        "2028-09-03", "2028-10-02", "2028-11-01", "2028-11-30",
        "2028-12-30",
    ],
    2029: [
        "2029-01-29", "2029-02-27", "2029-03-29", "2029-04-28",
        "2029-05-27", "2029-06-26", "2029-07-25", "2029-08-24",
        "2029-09-22", "2029-10-22", "2029-11-20", "2029-12-20",
    ],
    2030: [
        "2030-01-18", "2030-02-17", "2030-03-19", "2030-04-17",
        "2030-05-17", "2030-06-15", "2030-07-15", "2030-08-13",
        "2030-09-12", "2030-10-11", "2030-11-10", "2030-12-10",
    ],
}
