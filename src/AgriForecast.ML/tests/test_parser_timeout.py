"""Security regression tests for the HARTI PDF parser DoS guard (ClickUp
86cahef8f / S2): the per-PDF wall-clock parse timeout in
``agriforecast_ml.harti.parser.parse_pdf``.

What S2 added
-------------
A malicious or pathological PDF (degenerate table layout, a parse bomb that
slips past the 25 MB download cap because it is small-on-disk but expensive to
parse) could make pdfplumber/pdfminer spin and stall the whole ingestion pass.
``parse_pdf`` now runs the parse body under a wall-clock timeout
(AGRI_HARTI_PARSE_TIMEOUT_SECONDS, default 120s). A PDF that exceeds the budget
is treated exactly like any other unparseable PDF: WARN, empty result, ingestion
continues — never a hard failure.

These tests stub the parse body so no real PDF is opened.
"""
from __future__ import annotations

import sys
import time
from pathlib import Path

import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml.harti import parser as harti_parser  # noqa: E402
from agriforecast_ml.harti.parser import ParsedPrice  # noqa: E402


def test_fast_parse_returns_rows_unchanged(monkeypatch):
    """A normal (fast) parse returns its rows through the timeout wrapper."""
    sentinel = [ParsedPrice(
        date_str="2024-01-01", harti_label="Beans", raw_cell="100-120",
        min_price=100.0, max_price=120.0, market_name="Dambulla",
    )]
    monkeypatch.setattr(harti_parser, "_parse_pdf_impl", lambda p, d: sentinel)

    result = harti_parser.parse_pdf(Path("whatever.pdf"), "2024-01-01")
    assert result == sentinel


def test_slow_parse_times_out_and_returns_empty(monkeypatch, caplog):
    """A parse that exceeds the (tiny, test-set) timeout yields [] not a hang."""
    monkeypatch.setenv("AGRI_HARTI_PARSE_TIMEOUT_SECONDS", "0.2")

    def _slow(pdf_path, date_str):
        time.sleep(5)  # far longer than the 0.2s budget
        return [ParsedPrice(
            date_str=date_str, harti_label="Beans", raw_cell="1-2",
            min_price=1.0, max_price=2.0,
        )]

    monkeypatch.setattr(harti_parser, "_parse_pdf_impl", _slow)

    start = time.monotonic()
    result = harti_parser.parse_pdf(Path("slow.pdf"), "2024-01-01")
    elapsed = time.monotonic() - start

    assert result == [], "A timed-out parse must return an empty result, not rows"
    assert elapsed < 4, "parse_pdf must abandon the slow parse near the timeout, not wait for it"


def test_timeout_disabled_when_zero(monkeypatch):
    """AGRI_HARTI_PARSE_TIMEOUT_SECONDS=0 disables the watchdog (direct call)."""
    monkeypatch.setenv("AGRI_HARTI_PARSE_TIMEOUT_SECONDS", "0")
    sentinel = [ParsedPrice(
        date_str="2024-01-01", harti_label="Beans", raw_cell="5-6",
        min_price=5.0, max_price=6.0,
    )]
    monkeypatch.setattr(harti_parser, "_parse_pdf_impl", lambda p, d: sentinel)

    result = harti_parser.parse_pdf(Path("x.pdf"), "2024-01-01")
    assert result == sentinel


def test_default_timeout_is_generous_for_real_bulletins():
    """The default cap must be comfortably above a real HARTI PDF's parse time
    (sub-second) so a legitimate full-backfill CLI run is never truncated."""
    assert harti_parser._DEFAULT_PARSE_TIMEOUT_SECONDS >= 60


def test_malformed_env_falls_back_to_default(monkeypatch):
    monkeypatch.setenv("AGRI_HARTI_PARSE_TIMEOUT_SECONDS", "not-a-number")
    assert harti_parser._parse_timeout_seconds() == harti_parser._DEFAULT_PARSE_TIMEOUT_SECONDS
