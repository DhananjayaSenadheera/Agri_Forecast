"""
AgriForecast ML -- data-quality check tests (R1.1 P1 Step 5,
ClickUp 86cahef64, PRD Section 2.5).

All tests are hermetic: a mocked SQLAlchemy engine (MagicMock, matching the
style of test_canonical.py / test_harti_multimarket.py), no network, no
live DB. Live-DB verification is done separately (see task report).

Coverage:
  TestValidatePriceRow        -- shared source-agnostic row validator:
                                  non-positive price -> reject; min>max ->
                                  hold (quarantine, NOT a silent swap,
                                  pinned per the design-decision doc in
                                  data_quality.py); a clean row passes.
  TestHartiWriterWiredToValidator
                               -- upsert_harti_price_observations() actually
                                  calls the shared validator: rejects
                                  non-positive rows, quarantines min>max
                                  rows (IsUnitConfirmed=0), passes clean
                                  rows through with IsUnitConfirmed=1.
  TestGapReport                -- tiering (1-2/3-7/8+), Poya suppression
                                  (a Poya day inside a 1-day gap makes the
                                  gap vanish), cold-start skip for thin
                                  series, structured (non-raising) return.
  TestOutlierFlagging          -- rolling-90d IQR hold flags a spike,
                                  leaves normal variance alone, skips
                                  cold-start series, NEVER deletes (holds
                                  via IsUnitConfirmed=0), dry_run writes
                                  nothing, clear_outlier_hold is single-row
                                  and parameterized.
  TestNoLeakage                -- the outlier reference window for a
                                  candidate at T never includes T itself or
                                  anything >= T (backward-looking only).
  TestNoSourceDuplicates       -- real SQL shape (GROUP BY ... HAVING
                                  COUNT(DISTINCT Source) > 1); raises with
                                  offending triples listed; passes cleanly
                                  when the (mocked) DB returns none.
  TestMacroPointInTimeGuards   -- assert_vintage_sane /
                                  assert_effective_not_future: pure
                                  functions, future-publication and
                                  before-reference-period rejected.
"""
from __future__ import annotations

import sys
import uuid
from datetime import date, timedelta
from pathlib import Path
from unittest.mock import MagicMock

import pytest

# ---------------------------------------------------------------------------
# Path setup
# ---------------------------------------------------------------------------
ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import data_quality as dq  # noqa: E402
from agriforecast_ml.harti import loader as harti_loader  # noqa: E402
from agriforecast_ml.harti.parser import ParsedPrice  # noqa: E402


# ===========================================================================
# Mock engine helpers (mirrors test_canonical.py::_mock_engine_returning)
# ===========================================================================

def _mock_engine_returning(rows: list[tuple]) -> MagicMock:
    engine = MagicMock()
    conn = MagicMock()
    result = MagicMock()
    result.fetchall.return_value = rows
    result.scalar.return_value = None
    conn.execute.return_value = result
    engine.connect.return_value.__enter__.return_value = conn
    return engine


def _mock_engine_sequenced(payloads: list) -> MagicMock:
    """Each successive conn.execute() call returns the next payload in
    `payloads` (a list of row-lists, or a callable for a MagicMock result
    with .scalar()). Mirrors test_canonical's _engine_with_alias_and_null_rows
    pattern, generalised for an arbitrary number of SELECT calls.
    """
    engine = MagicMock()
    conn = MagicMock()
    state = {"n": 0}

    def _execute(*_a, **_k):
        idx = state["n"]
        state["n"] += 1
        res = MagicMock()
        payload = payloads[idx] if idx < len(payloads) else []
        if isinstance(payload, dict) and "scalar" in payload:
            res.scalar.return_value = payload["scalar"]
            res.fetchall.return_value = []
            res.fetchone.return_value = None
        elif isinstance(payload, dict) and "fetchone" in payload:
            res.fetchone.return_value = payload["fetchone"]
            res.fetchall.return_value = []
            res.scalar.return_value = None
        else:
            res.fetchall.return_value = payload
            res.scalar.return_value = None
            res.fetchone.return_value = None
        return res

    conn.execute.side_effect = _execute
    engine.connect.return_value.__enter__.return_value = conn
    begin_conn = MagicMock()
    engine.begin.return_value.__enter__.return_value = begin_conn
    return engine, conn, begin_conn


def _mock_engine_for_harti_upsert(existing_key_rows: "list[tuple] | None" = None) -> MagicMock:
    """Mock engine shaped for harti_loader.upsert_harti_price_observations()'s
    real (non-dry-run) write path specifically.

    That function's existing-keys SELECT ("does this (MarketId,
    ExternalCommodityName, ObservedDate) already exist?") runs INSIDE
    `with engine.begin() as conn:` (see loader.py ~line 763), NOT inside
    `engine.connect()` -- unlike every other read in this codebase. A plain
    _mock_engine_sequenced() wires payloads onto the `engine.connect()`
    connection, which this code path never touches, so it always silently
    sees an empty existing_rows result (MagicMock's default `__iter__`
    yields nothing) and takes the INSERT branch regardless of what payload
    was requested. This helper wires the SAME begin_conn used for the
    UPDATE/INSERT statements themselves to also serve the existing-keys
    SELECT as its first call, matching the real call order.
    """
    engine = MagicMock()
    begin_conn = MagicMock()
    state = {"n": 0}
    existing_key_rows = existing_key_rows or []

    def _execute(*_a, **_k):
        idx = state["n"]
        state["n"] += 1
        res = MagicMock()
        if idx == 0:
            # The existing-keys SELECT (always the first execute() inside
            # the `with engine.begin()` block).
            res.fetchall.return_value = existing_key_rows
        else:
            # Subsequent calls are the actual UPDATE/INSERT statements --
            # their return value is never read by the loader.
            res.fetchall.return_value = []
        return res

    begin_conn.execute.side_effect = _execute
    engine.begin.return_value.__enter__.return_value = begin_conn
    # engine.connect() is unused by the real (non-dry-run) write path once
    # _build_market_map/CommodityAliasResolver are monkeypatched out (as
    # every test using this helper does) -- still wire it defensively so an
    # accidental extra read doesn't raise.
    conn = MagicMock()
    conn.execute.return_value.fetchall.return_value = []
    engine.connect.return_value.__enter__.return_value = conn
    return engine, begin_conn


BEANS_CROP_ID = uuid.UUID("aaaaaaaa-0000-0000-0000-000000000001")
DAMBULLA_MARKET_ID = uuid.UUID("bbbbbbbb-0000-0000-0000-000000000001")


# ===========================================================================
# 1. validate_price_row -- shared, source-agnostic
# ===========================================================================

class TestValidatePriceRow:
    def test_clean_row_is_accepted_not_rejected_not_held(self):
        result = dq.validate_price_row(
            min_price=100.0, max_price=120.0, source="HARTI",
            crop_label="Beans", observed_date=date(2025, 1, 1),
            market_name="Dambulla",
        )
        assert result.accepted is True
        assert result.reject is False
        assert result.hold is False
        assert result.reason == "ok"

    @pytest.mark.parametrize("min_price,max_price", [
        (0.0, 100.0), (100.0, 0.0), (-5.0, 100.0), (100.0, -5.0), (0.0, 0.0),
    ])
    def test_non_positive_price_is_rejected(self, min_price, max_price):
        result = dq.validate_price_row(
            min_price=min_price, max_price=max_price, source="HARTI",
            crop_label="Beans", observed_date=date(2025, 1, 1),
        )
        assert result.accepted is False
        assert result.reject is True
        assert result.hold is False
        assert result.reason == "non_positive_price"
        assert "non-positive" in result.message

    def test_min_greater_than_max_is_quarantined_not_swapped(self):
        """Pins the design decision (data_quality.py Section 2): min>max is
        HELD (accepted=True, hold=True), never silently swapped. The
        returned min/max are NOT touched by this function at all -- it is
        the caller's job to write IsUnitConfirmed=0, this function only
        signals that it must."""
        result = dq.validate_price_row(
            min_price=300.0, max_price=200.0, source="CBSL",
            crop_label="Some Future Source Crop", observed_date=date(2025, 1, 1),
        )
        assert result.accepted is True
        assert result.reject is False
        assert result.hold is True
        assert result.reason == "min_greater_than_max"
        assert "300.0" in result.message and "200.0" in result.message
        assert "not silently swapping" in result.message

    def test_min_equal_max_is_fine(self):
        """A single-point price cell (min==max) is not ambiguous -- no hold."""
        result = dq.validate_price_row(
            min_price=100.0, max_price=100.0, source="HARTI",
            crop_label="Beans", observed_date=date(2025, 1, 1),
        )
        assert result.accepted is True
        assert result.hold is False

    def test_is_source_agnostic_signature_works_for_a_future_source(self):
        """The validator must not special-case source='HARTI' internally --
        exercised here with a hypothetical future source string."""
        result = dq.validate_price_row(
            min_price=50.0, max_price=60.0, source="CBSL",
            crop_label="Rice", observed_date=date(2026, 1, 1),
            market_name="National",
        )
        assert result.accepted is True
        assert result.reason == "ok"


# ===========================================================================
# 2. Wired into upsert_harti_price_observations()
# ===========================================================================

class TestHartiWriterWiredToValidator:
    def _fake_market_map(self):
        return {"Dambulla": DAMBULLA_MARKET_ID}

    def _patched(self, monkeypatch, resolve_return=BEANS_CROP_ID):
        monkeypatch.setattr(
            harti_loader, "_build_market_map", lambda _engine: self._fake_market_map()
        )
        fake_resolver = MagicMock()
        fake_resolver.resolve.return_value = resolve_return
        monkeypatch.setattr(
            harti_loader, "CommodityAliasResolver", lambda _engine: fake_resolver
        )

    def test_non_positive_price_row_is_rejected_and_warned(self, monkeypatch, caplog):
        self._patched(monkeypatch)
        rows = [ParsedPrice("2019-01-01", "Beans", "0-0", 0.0, 0.0, market_name="Dambulla")]

        result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)

        assert result["inserted"] == 0
        assert result["skipped_invalid_price"] == 1
        assert any(
            "non-positive" in rec.message and rec.levelname == "WARNING"
            for rec in caplog.records
        )

    def test_min_greater_than_max_row_is_quarantined_not_dropped(self, monkeypatch):
        """A held row must still be inserted (not skipped) -- ambiguous
        price data is still evidence something happened that day, and
        dropping it would fabricate a gap. It just can't reach features
        until confirmed (IsUnitConfirmed=0)."""
        self._patched(monkeypatch)
        rows = [ParsedPrice("2019-01-01", "Beans", "300-200", 300.0, 200.0, market_name="Dambulla")]

        # Use a real (non-dry-run) mocked engine so we can inspect the
        # actual INSERT payload's is_unit_confirmed value. No existing key
        # rows -> takes the INSERT branch.
        engine, begin_conn = _mock_engine_for_harti_upsert(existing_key_rows=[])
        result = harti_loader.upsert_harti_price_observations(rows, engine=engine, dry_run=False)

        assert result["inserted"] == 1
        insert_call = begin_conn.execute.call_args_list[-1]
        params = insert_call[0][1]
        assert params["is_unit_confirmed"] is False
        assert params["min_price"] == 300.0  # NOT swapped
        assert params["max_price"] == 200.0  # NOT swapped

    def test_min_greater_than_max_logs_warning_with_numbers(self, monkeypatch, caplog):
        self._patched(monkeypatch)
        rows = [ParsedPrice("2019-01-01", "Beans", "300-200", 300.0, 200.0, market_name="Dambulla")]

        harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)

        assert any(
            "300.0" in rec.message and "200.0" in rec.message
            and "HOLDING" in rec.message
            for rec in caplog.records
        )

    def test_clean_row_sets_is_unit_confirmed_true(self, monkeypatch):
        self._patched(monkeypatch)
        rows = [ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla")]

        engine, begin_conn = _mock_engine_for_harti_upsert(existing_key_rows=[])
        result = harti_loader.upsert_harti_price_observations(rows, engine=engine, dry_run=False)

        assert result["inserted"] == 1
        insert_call = begin_conn.execute.call_args_list[-1]
        params = insert_call[0][1]
        assert params["is_unit_confirmed"] is True


# ===========================================================================
# 2b. Sticky-hold regression (reviewer BLOCKING finding, R1.1 P1 Step 5):
# a routine re-ingest of an already-quarantined row must NOT silently raise
# IsUnitConfirmed back to 1. Only clear_outlier_hold() may release a hold.
# ===========================================================================

class TestUpdateBranchNeverSilentlyUnholdsQuarantinedRows:
    """Exercises the UPDATE branch specifically (the existing-key path),
    which is where the reviewer's live repro found the bug: the UPDATE SQL
    unconditionally overwrote IsUnitConfirmed with this run's freshly
    computed value, so a clean re-parse of a previously-held row erased the
    hold. The fix is a CASE-based ratchet in the SQL itself (mirrors the
    CropId COALESCE pattern already used for the same "never silently
    re-map/un-hold on a routine re-run" reason)."""

    def _fake_market_map(self):
        return {"Dambulla": DAMBULLA_MARKET_ID}

    def _patched(self, monkeypatch, resolve_return=BEANS_CROP_ID):
        monkeypatch.setattr(
            harti_loader, "_build_market_map", lambda _engine: self._fake_market_map()
        )
        fake_resolver = MagicMock()
        fake_resolver.resolve.return_value = resolve_return
        monkeypatch.setattr(
            harti_loader, "CommodityAliasResolver", lambda _engine: fake_resolver
        )

    def _existing_key_row(self):
        """Shape matches the loader's existing-keys SELECT:
        (MarketId, ExternalCommodityName, ObservedDate)."""
        return (str(DAMBULLA_MARKET_ID).upper(), "Beans", "2019-01-01")

    def test_update_sql_never_unconditionally_overwrites_is_unit_confirmed(self, monkeypatch):
        """Direct pin on the SQL shape itself: the UPDATE statement must use
        the CASE-based ratchet, not a bare `IsUnitConfirmed = :is_unit_confirmed`."""
        self._patched(monkeypatch)
        rows = [ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla")]

        engine, begin_conn = _mock_engine_for_harti_upsert(
            existing_key_rows=[self._existing_key_row()]
        )
        result = harti_loader.upsert_harti_price_observations(rows, engine=engine, dry_run=False)

        assert result["updated"] == 1
        update_call = begin_conn.execute.call_args_list[-1]
        update_sql = str(update_call[0][0])
        assert "CASE" in update_sql
        assert "WHEN IsUnitConfirmed = 0 THEN 0" in update_sql
        # The ratchet's ELSE branch is where the bind param is actually
        # used -- a bare `IsUnitConfirmed = :is_unit_confirmed` (no CASE at
        # all) is exactly the bug this regression pins against.
        assert "ELSE :is_unit_confirmed" in update_sql
        assert "END" in update_sql

    def test_reparsing_a_clean_row_does_not_raise_a_held_rows_flag_in_the_db(self, monkeypatch):
        """The decisive regression: simulate the exact scenario the
        reviewer proved live -- a row is already quarantined (IsUnitConfirmed=0
        in the real DB), and this loader re-runs over the SAME clean parsed
        row (validation.hold=False -> is_unit_confirmed=True is what THIS
        run computes). The UPDATE statement sent to the DB must carry the
        CASE ratchet (proven above) rather than the bare overwrite -- which
        is what makes the hold survive in the real database regardless of
        what value this run computed. This test pins the call-site behavior
        (params still carry is_unit_confirmed=True, the SQL still has the
        ratchet) so a future edit that reintroduces a bare SET cannot pass
        silently."""
        self._patched(monkeypatch)
        rows = [ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla")]

        engine, begin_conn = _mock_engine_for_harti_upsert(
            existing_key_rows=[self._existing_key_row()]
        )
        result = harti_loader.upsert_harti_price_observations(rows, engine=engine, dry_run=False)

        assert result["updated"] == 1
        update_call = begin_conn.execute.call_args_list[-1]
        update_sql = str(update_call[0][0])
        params = update_call[0][1]

        # This run's OWN computed flag is True (clean row) -- exactly the
        # scenario that used to silently erase a hold.
        assert params["is_unit_confirmed"] is True
        # But the SQL sent to the DB is the ratchet, not a bare overwrite --
        # so a DB-side IsUnitConfirmed=0 stays 0 no matter what this
        # parameter says. (The ratchet's correctness under a real DB engine
        # is exactly what the CASE WHEN IsUnitConfirmed = 0 THEN 0 clause
        # guarantees; this mocked test cannot execute real SQL, so it pins
        # the SQL shape actually sent, which is the load-bearing artifact.)
        assert "CASE" in update_sql and "WHEN IsUnitConfirmed = 0 THEN 0" in update_sql

    def test_incoming_min_greater_than_max_can_still_lower_flag_to_zero(self, monkeypatch):
        """The ratchet is one-way (0 stays 0), but NOT two-way-frozen: a
        previously-clean row (DB currently IsUnitConfirmed=1) that this run
        discovers is now ambiguous (min>max) must still be able to
        transition 1 -> 0 in the same write -- the CASE's ELSE arm takes
        the incoming (held -> False) value normally."""
        self._patched(monkeypatch)
        rows = [ParsedPrice("2019-01-01", "Beans", "300-200", 300.0, 200.0, market_name="Dambulla")]

        engine, begin_conn = _mock_engine_for_harti_upsert(
            existing_key_rows=[self._existing_key_row()]
        )
        result = harti_loader.upsert_harti_price_observations(rows, engine=engine, dry_run=False)

        assert result["updated"] == 1
        update_call = begin_conn.execute.call_args_list[-1]
        params = update_call[0][1]
        update_sql = str(update_call[0][0])

        # This run computed hold=True -> is_unit_confirmed=False; the ELSE
        # arm of the ratchet applies it (DB row was NOT already held, or --
        # if it WAS already held -- stays held either way; either path
        # lands on 0, which is what we assert on the bound parameter and
        # the SQL shape here).
        assert params["is_unit_confirmed"] is False
        assert params["min_price"] == 300.0  # not swapped
        assert params["max_price"] == 200.0  # not swapped
        assert "CASE" in update_sql and "ELSE :is_unit_confirmed" in update_sql

    def test_insert_branch_unaffected_new_row_has_no_prior_hold_to_protect(self, monkeypatch):
        """Sanity check: the INSERT branch (brand-new key, no existing row)
        has nothing to ratchet against -- it must keep writing the plain
        computed value directly, matching TestHartiWriterWiredToValidator's
        existing INSERT-path assertions. Pinned here explicitly so a future
        change cannot accidentally apply the CASE ratchet to INSERT (which
        would be meaningless -- IsUnitConfirmed doesn't exist yet on that row)."""
        self._patched(monkeypatch)
        rows = [ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla")]

        engine, begin_conn = _mock_engine_for_harti_upsert(existing_key_rows=[])  # -> INSERT
        result = harti_loader.upsert_harti_price_observations(rows, engine=engine, dry_run=False)

        assert result["inserted"] == 1
        insert_call = begin_conn.execute.call_args_list[-1]
        insert_sql = str(insert_call[0][0])
        params = insert_call[0][1]
        assert "CASE" not in insert_sql
        assert params["is_unit_confirmed"] is True


# ===========================================================================
# 3. gap_report()
# ===========================================================================

class TestGapReport:
    def _rows(self, crop, market_id, market_name, source, dates):
        return [(crop, str(market_id), market_name, source, d) for d in dates]

    def test_one_day_gap_is_info_tier(self):
        # 2025-01-01, skip 01-02, 01-03 (1 missing day)
        dates = [date(2025, 1, 1), date(2025, 1, 3)] + [
            date(2025, 1, 4) + timedelta(days=i) for i in range(6)
        ]  # pad to satisfy MIN_OBSERVATIONS_FOR_GAP_SCAN
        engine = _mock_engine_returning(
            self._rows("Beans", DAMBULLA_MARKET_ID, "Dambulla DEC", "HARTI", dates)
        )
        report = dq.gap_report(engine, source="HARTI")

        assert report["n_info"] == 1
        assert report["n_warning"] == 0
        assert report["n_error"] == 0
        entry = report["entries"][0]
        assert entry["missing_days"] == 1
        assert entry["tier"] == "INFO"

    def test_five_day_gap_is_warning_tier(self):
        dates = [date(2025, 1, 1), date(2025, 1, 7)] + [
            date(2025, 1, 8) + timedelta(days=i) for i in range(6)
        ]
        engine = _mock_engine_returning(
            self._rows("Beans", DAMBULLA_MARKET_ID, "Dambulla DEC", "HARTI", dates)
        )
        report = dq.gap_report(engine, source="HARTI")

        assert report["n_warning"] == 1
        entry = [e for e in report["entries"] if e["missing_days"] == 5][0]
        assert entry["tier"] == "WARNING"

    def test_ten_day_gap_is_error_tier_and_does_not_raise(self):
        """ERROR tier is a structured report entry requiring manual ack --
        NOT an exception. gap_report must return normally."""
        dates = [date(2025, 1, 1), date(2025, 1, 12)] + [
            date(2025, 1, 13) + timedelta(days=i) for i in range(6)
        ]
        engine = _mock_engine_returning(
            self._rows("Beans", DAMBULLA_MARKET_ID, "Dambulla DEC", "HARTI", dates)
        )
        report = dq.gap_report(engine, source="HARTI")  # must not raise

        assert report["n_error"] == 1
        entry = [e for e in report["entries"] if e["missing_days"] == 10][0]
        assert entry["tier"] == "ERROR"

    def test_tier_boundaries_exact(self):
        assert dq._gap_tier(1) == "INFO"
        assert dq._gap_tier(2) == "INFO"
        assert dq._gap_tier(3) == "WARNING"
        assert dq._gap_tier(7) == "WARNING"
        assert dq._gap_tier(8) == "ERROR"
        assert dq._gap_tier(100) == "ERROR"

    def test_poya_day_inside_a_one_day_gap_makes_it_vanish_entirely(self):
        """2025-01-13 is a real configured Poya day. A gap of exactly that
        one missing day must produce ZERO gap entries (not even INFO)."""
        dates = [date(2025, 1, 12), date(2025, 1, 14)] + [
            date(2025, 1, 15) + timedelta(days=i) for i in range(6)
        ]
        engine = _mock_engine_returning(
            self._rows("Beans", DAMBULLA_MARKET_ID, "Dambulla DEC", "HARTI", dates)
        )
        report = dq.gap_report(engine, source="HARTI")

        assert report["entries"] == []
        assert report["n_info"] == 0

    def test_poya_day_inside_a_multi_day_gap_reduces_missing_count(self):
        """A gap containing 1 Poya day among several missing days reports
        missing_days reduced by exactly 1, and records poya_suppressed_days."""
        # Gap: 2025-01-12 -> 2025-01-17 (missing 13,14,15,16 = 4 days).
        # 2025-01-13 is Poya -> effective missing_days = 3.
        dates = [date(2025, 1, 12), date(2025, 1, 17)] + [
            date(2025, 1, 18) + timedelta(days=i) for i in range(6)
        ]
        engine = _mock_engine_returning(
            self._rows("Beans", DAMBULLA_MARKET_ID, "Dambulla DEC", "HARTI", dates)
        )
        report = dq.gap_report(engine, source="HARTI")

        assert len(report["entries"]) == 1
        entry = report["entries"][0]
        assert entry["missing_days"] == 3
        assert entry["poya_suppressed_days"] == 1
        assert entry["tier"] == "WARNING"

    def test_series_with_too_few_observations_is_cold_start_not_a_gap(self):
        dates = [date(2025, 1, 1), date(2025, 1, 5)]  # only 2 obs
        engine = _mock_engine_returning(
            self._rows("Winged Bean", DAMBULLA_MARKET_ID, "Dambulla DEC", "HARTI", dates)
        )
        report = dq.gap_report(engine, source="HARTI")

        assert report["entries"] == []
        assert len(report["insufficient_history"]) == 1
        assert report["insufficient_history"][0]["crop_label"] == "Winged Bean"
        assert report["insufficient_history"][0]["n_observations"] == 2

    def test_no_gaps_when_series_is_contiguous(self):
        dates = [date(2025, 1, 1) + timedelta(days=i) for i in range(10)]
        engine = _mock_engine_returning(
            self._rows("Beans", DAMBULLA_MARKET_ID, "Dambulla DEC", "HARTI", dates)
        )
        report = dq.gap_report(engine, source="HARTI")

        assert report["entries"] == []
        assert report["n_info"] == report["n_warning"] == report["n_error"] == 0

    def test_returns_structured_dict_never_raises_for_findings(self):
        dates = [date(2025, 1, 1), date(2025, 3, 1)] + [
            date(2025, 3, 2) + timedelta(days=i) for i in range(6)
        ]
        engine = _mock_engine_returning(
            self._rows("Beans", DAMBULLA_MARKET_ID, "Dambulla DEC", "HARTI", dates)
        )
        report = dq.gap_report(engine, source="HARTI")  # a huge gap -- still no raise
        assert isinstance(report, dict)
        assert "entries" in report and "series_scanned" in report


# ===========================================================================
# 4. flag_price_outliers() / clear_outlier_hold()
# ===========================================================================

class TestOutlierFlagging:
    def _make_rows(self, crop_id, market_id, ext_name, base_date, midpoints):
        """Builds (Id, CropId, MarketId, ExternalCommodityName, ObservedDate,
        MinPrice, MaxPrice) rows from a list of midpoints, one per day
        starting at base_date, min=max=midpoint (single-point price -- the
        outlier check only reads the midpoint anyway)."""
        rows = []
        for i, mid in enumerate(midpoints):
            rows.append((
                uuid.uuid4(), str(crop_id), str(market_id), ext_name,
                base_date + timedelta(days=i), mid, mid,
            ))
        return rows

    def test_stable_series_has_no_flags(self):
        # 30 days of essentially flat price around 100 (small noise)
        midpoints = [100.0, 101.0, 99.0, 100.0, 102.0] * 6
        rows = self._make_rows(BEANS_CROP_ID, DAMBULLA_MARKET_ID, "Beans", date(2025, 1, 1), midpoints)
        engine = _mock_engine_returning(rows)

        result = dq.flag_price_outliers(engine, source="HARTI", dry_run=True)

        assert result["n_flagged"] == 0
        assert result["series_checked"] == 1

    def test_spike_far_above_rolling_median_is_flagged(self):
        # 25 stable days around 100, then one massive spike to 5000
        midpoints = [100.0, 101.0, 99.0, 100.0, 102.0] * 5 + [5000.0]
        rows = self._make_rows(BEANS_CROP_ID, DAMBULLA_MARKET_ID, "Beans", date(2025, 1, 1), midpoints)
        engine = _mock_engine_returning(rows)

        result = dq.flag_price_outliers(engine, source="HARTI", dry_run=True)

        assert result["n_flagged"] == 1
        flagged = result["flagged"][0]
        assert flagged["midpoint"] == 5000.0
        assert flagged["observed_date"] == (date(2025, 1, 1) + timedelta(days=25)).isoformat()

    def test_cold_start_series_under_min_observations_is_skipped_not_flagged(self):
        # Fewer than MIN_OBSERVATIONS_FOR_OUTLIER_CHECK (20) total observations,
        # including a wild spike -- must be skipped entirely, not flagged.
        midpoints = [100.0, 105.0, 95.0, 5000.0]  # 4 obs, well under 20
        rows = self._make_rows(BEANS_CROP_ID, DAMBULLA_MARKET_ID, "Beans", date(2025, 1, 1), midpoints)
        engine = _mock_engine_returning(rows)

        result = dq.flag_price_outliers(engine, source="HARTI", dry_run=True)

        assert result["n_flagged"] == 0
        assert result["series_skipped_cold_start"] == 1
        assert result["series_checked"] == 0

    def test_dry_run_does_not_write(self):
        midpoints = [100.0, 101.0, 99.0, 100.0, 102.0] * 5 + [5000.0]
        rows = self._make_rows(BEANS_CROP_ID, DAMBULLA_MARKET_ID, "Beans", date(2025, 1, 1), midpoints)
        engine, conn, begin_conn = _mock_engine_sequenced([rows])

        result = dq.flag_price_outliers(engine, source="HARTI", dry_run=True)

        assert result["n_flagged"] == 1
        engine.begin.assert_not_called()

    def test_non_dry_run_writes_isunitconfirmed_zero_for_flagged_rows_only(self):
        midpoints = [100.0, 101.0, 99.0, 100.0, 102.0] * 5 + [5000.0]
        rows = self._make_rows(BEANS_CROP_ID, DAMBULLA_MARKET_ID, "Beans", date(2025, 1, 1), midpoints)
        engine, conn, begin_conn = _mock_engine_sequenced([rows])

        result = dq.flag_price_outliers(engine, source="HARTI", dry_run=False)

        assert result["n_flagged"] == 1
        assert begin_conn.execute.call_count == 1  # exactly one UPDATE, for the one flagged row
        update_sql = str(begin_conn.execute.call_args[0][0])
        assert "IsUnitConfirmed = 0" in update_sql
        assert "WHERE Id = :id" in update_sql

    def test_rows_with_null_cropid_are_excluded_by_query_filter(self):
        """The SELECT itself filters CropId IS NOT NULL -- verify the
        executed SQL enforces that (an outlier fence needs a stable
        per-crop identity)."""
        engine = _mock_engine_returning([])
        dq.flag_price_outliers(engine, source="HARTI", dry_run=True)

        conn = engine.connect.return_value.__enter__.return_value
        executed_sql = str(conn.execute.call_args[0][0])
        assert "CropId IS NOT NULL" in executed_sql

    def test_clear_outlier_hold_is_single_row_parameterized(self):
        engine = MagicMock()
        begin_conn = MagicMock()
        begin_conn.execute.return_value.rowcount = 1
        engine.begin.return_value.__enter__.return_value = begin_conn

        row_id = uuid.uuid4()
        result = dq.clear_outlier_hold(row_id, engine=engine)

        assert result is True
        assert begin_conn.execute.call_count == 1
        call_sql = str(begin_conn.execute.call_args[0][0])
        call_params = begin_conn.execute.call_args[0][1]
        assert "IsUnitConfirmed = 1" in call_sql
        assert "WHERE Id = :id" in call_sql
        assert call_params == {"id": str(row_id)}

    def test_clear_outlier_hold_returns_false_when_no_row_matched(self):
        engine = MagicMock()
        begin_conn = MagicMock()
        begin_conn.execute.return_value.rowcount = 0
        engine.begin.return_value.__enter__.return_value = begin_conn

        result = dq.clear_outlier_hold(uuid.uuid4(), engine=engine)
        assert result is False


# ===========================================================================
# 5. No-leakage guard on the outlier rolling window
# ===========================================================================

class TestNoLeakage:
    def test_reference_window_never_includes_the_candidate_or_future_rows(self):
        """Construct a series where a LATER spike would, if leaked
        backward, poison the median for an EARLIER candidate. Prove the
        earlier candidate is unaffected."""
        base = date(2025, 1, 1)
        # 25 days of small, realistic noise (IQR > 0, matching real HARTI
        # day-to-day variance), then a spike on day 26 (index 25), then 5
        # more stable days AFTER the spike.
        midpoints = [100.0, 101.0, 99.0, 100.0, 102.0] * 5 + [9999.0] + [100.0, 101.0, 99.0, 100.0, 102.0]
        rows = []
        for i, mid in enumerate(midpoints):
            rows.append((
                uuid.uuid4(), str(BEANS_CROP_ID), str(DAMBULLA_MARKET_ID),
                "Beans", base + timedelta(days=i), mid, mid,
            ))
        engine = _mock_engine_returning(rows)

        result = dq.flag_price_outliers(engine, source="HARTI", dry_run=True)

        # Only the spike itself (day index 25) should be flagged -- if the
        # spike had leaked backward into any earlier candidate's window,
        # nothing earlier would fire anyway (it's stable), but if it leaked
        # FORWARD (wrongly influencing candidates after it) or into its own
        # window, we would see more than one flag or none. Also check the
        # observed_date of the sole flag matches the spike's date exactly.
        assert result["n_flagged"] == 1
        assert result["flagged"][0]["observed_date"] == (base + timedelta(days=25)).isoformat()
        assert result["flagged"][0]["midpoint"] == 9999.0

    def test_window_start_boundary_is_strictly_before_not_inclusive_of_candidate(self):
        """Directly exercise _iqr_median plumbing indirectly: a candidate
        at T=90 days after series start must not include a same-day
        duplicate-date row of itself. This is implicitly covered by the
        `< t` (not `<= t`) condition in flag_price_outliers -- pinned here
        via a series where day 0 and the last day share a date-adjacent
        boundary at exactly the 90-day window edge."""
        base = date(2025, 1, 1)
        # 95 days of small realistic noise (IQR > 0) then a spike --
        # window_start for the spike (day index 95) is day index 5
        # (95-90), so days 0-4 are excluded from its reference (correctly,
        # since they're outside 90 days), and day 95 itself (the
        # candidate) must never be in its own reference set regardless.
        midpoints = [100.0, 101.0, 99.0, 100.0, 102.0] * 19 + [9999.0]
        rows = []
        for i, mid in enumerate(midpoints):
            rows.append((
                uuid.uuid4(), str(BEANS_CROP_ID), str(DAMBULLA_MARKET_ID),
                "Beans", base + timedelta(days=i), mid, mid,
            ))
        engine = _mock_engine_returning(rows)

        result = dq.flag_price_outliers(engine, source="HARTI", dry_run=True)
        assert result["n_flagged"] == 1
        assert result["flagged"][0]["midpoint"] == 9999.0


# ===========================================================================
# 6. assert_no_source_duplicates()
# ===========================================================================

DAMBULLA_MARKET_ID = "11111111-1111-1111-1111-111111111111"
PETTAH_MARKET_ID = "22222222-2222-2222-2222-222222222222"


def _resolve_dambulla_payload(market_id: str = DAMBULLA_MARKET_ID) -> dict:
    """The resolve_market_id_by_code(MKT00000001) payload -- ALWAYS the
    first conn.execute() call assert_no_source_duplicates() issues, per
    SF2 (reviewer should-fix): the {HARTI, DAMBULLA_DEC} coexistence
    allowance is scoped to this resolved Dambulla market id."""
    return {"fetchone": (market_id,)}


class TestNoSourceDuplicates:
    def test_passes_cleanly_when_no_duplicates_found(self):
        engine, conn, _ = _mock_engine_sequenced(
            [_resolve_dambulla_payload(), [], {"scalar": 42}])
        result = dq.assert_no_source_duplicates(engine)
        assert result == 42

    def test_raises_with_offending_triples_when_duplicates_found(self):
        dup_row = ("crop-1", "2025-01-01", "market-1", 2)
        engine, conn, _ = _mock_engine_sequenced(
            [_resolve_dambulla_payload(), [dup_row]])

        with pytest.raises(AssertionError) as exc_info:
            dq.assert_no_source_duplicates(engine)

        msg = str(exc_info.value)
        assert "SOURCE DUPLICATION" in msg
        assert "crop-1" in msg
        assert "2025-01-01" in msg
        assert "market-1" in msg

    def test_query_shape_is_real_group_by_having_count_distinct_source(self):
        engine, conn, _ = _mock_engine_sequenced(
            [_resolve_dambulla_payload(), [], {"scalar": 0}])
        dq.assert_no_source_duplicates(engine)

        # Call 0 is the Dambulla market-id resolve; call 1 is the candidate
        # (CropId, ObservedDate, MarketId, Source) query.
        candidate_call_sql = str(conn.execute.call_args_list[1][0][0])
        assert "GROUP BY CropId, ObservedDate, MarketId" in candidate_call_sql
        assert "HAVING COUNT(DISTINCT Source) > 1" in candidate_call_sql
        assert "CropId IS NOT NULL" in candidate_call_sql

    def test_scoped_to_cropid_not_null(self):
        """An unresolved-crop row (CropId IS NULL) must not be considered
        for cross-source duplication -- it has no stable dedup identity."""
        engine, conn, _ = _mock_engine_sequenced(
            [_resolve_dambulla_payload(), [], {"scalar": 0}])
        dq.assert_no_source_duplicates(engine)
        candidate_call_sql = str(conn.execute.call_args_list[1][0][0])
        assert "CropId IS NOT NULL" in candidate_call_sql

    def test_missing_dambulla_market_row_raises(self):
        """Fail-closed: if the Dambulla Markets row cannot be resolved, the
        check must raise rather than silently applying (or silently NOT
        applying) the coexistence allowance."""
        engine, conn, _ = _mock_engine_sequenced([{"fetchone": None}])
        with pytest.raises(RuntimeError, match="MKT00000001"):
            dq.assert_no_source_duplicates(engine)

    # -----------------------------------------------------------------
    # SF2 (reviewer should-fix): the {HARTI, DAMBULLA_DEC} allowance is
    # scoped to the DAMBULLA market only.
    # -----------------------------------------------------------------
    def test_harti_dec_pair_at_dambulla_is_allowed(self):
        """The adjudicated overlap (exactly {HARTI, DAMBULLA_DEC} AT the
        resolved Dambulla market) must NOT raise."""
        dup_rows = [
            ("crop-1", "2025-06-01", DAMBULLA_MARKET_ID, "HARTI"),
            ("crop-1", "2025-06-01", DAMBULLA_MARKET_ID, "DAMBULLA_DEC"),
        ]
        engine, conn, _ = _mock_engine_sequenced(
            [_resolve_dambulla_payload(), dup_rows, {"scalar": 100}])
        result = dq.assert_no_source_duplicates(engine)  # must not raise
        assert result == 100

    def test_harti_dec_pair_at_a_non_dambulla_market_still_raises(self):
        """SF2's actual target: the SAME allowed source pair appearing at a
        market OTHER than Dambulla (e.g. a hypothetical mis-resolved-market
        mirror bug writing DEC rows at Pettah) must still be flagged as a
        real violation, not silently absorbed by the carve-out."""
        dup_rows = [
            ("crop-1", "2025-06-01", PETTAH_MARKET_ID, "HARTI"),
            ("crop-1", "2025-06-01", PETTAH_MARKET_ID, "DAMBULLA_DEC"),
        ]
        engine, conn, _ = _mock_engine_sequenced(
            [_resolve_dambulla_payload(), dup_rows])

        with pytest.raises(AssertionError) as exc_info:
            dq.assert_no_source_duplicates(engine)

        msg = str(exc_info.value)
        assert "SOURCE DUPLICATION" in msg
        assert "crop-1" in msg
        assert PETTAH_MARKET_ID in msg

    def test_dambulla_market_id_resolved_by_code_not_hardcoded(self):
        engine, conn, _ = _mock_engine_sequenced(
            [_resolve_dambulla_payload(), [], {"scalar": 0}])
        dq.assert_no_source_duplicates(engine)
        first_call_sql = str(conn.execute.call_args_list[0][0][0])
        assert "MarketCode" in first_call_sql
        first_call_params = conn.execute.call_args_list[0][0][1]
        assert first_call_params == {"code": dq._DAMBULLA_MARKET_CODE}

    def test_market_id_comparison_is_case_insensitive(self):
        """SQL Server GUID string casing can vary by driver -- the Dambulla
        match must not falsely reject a same-GUID-different-case pairing."""
        dup_rows = [
            ("crop-1", "2025-06-01", DAMBULLA_MARKET_ID.upper(), "HARTI"),
            ("crop-1", "2025-06-01", DAMBULLA_MARKET_ID.upper(), "DAMBULLA_DEC"),
        ]
        engine, conn, _ = _mock_engine_sequenced(
            [_resolve_dambulla_payload(DAMBULLA_MARKET_ID.lower()), dup_rows, {"scalar": 1}])
        result = dq.assert_no_source_duplicates(engine)  # must not raise
        assert result == 1


# ===========================================================================
# 7. Macro point-in-time guards (P3 stub -- pure functions, no DB)
# ===========================================================================

class TestMacroPointInTimeGuards:
    def test_vintage_sane_accepts_publication_after_reference_start_and_not_future(self):
        # No exception -> pass
        dq.assert_vintage_sane(
            publication_date=date(2026, 7, 5),
            reference_period_start=date(2026, 6, 1),
            now=date(2026, 7, 10),
        )

    def test_vintage_sane_rejects_publication_in_the_future(self):
        with pytest.raises(ValueError, match="future"):
            dq.assert_vintage_sane(
                publication_date=date(2026, 8, 1),
                reference_period_start=date(2026, 6, 1),
                now=date(2026, 7, 10),
            )

    def test_vintage_sane_rejects_publication_before_reference_period_start(self):
        """Cannot publish a CPI figure for June 2026 in May 2026 -- the
        period had not even started yet."""
        with pytest.raises(ValueError, match="has not started"):
            dq.assert_vintage_sane(
                publication_date=date(2026, 5, 15),
                reference_period_start=date(2026, 6, 1),
                now=date(2026, 7, 10),
            )

    def test_vintage_sane_allows_publication_exactly_on_reference_start(self):
        dq.assert_vintage_sane(
            publication_date=date(2026, 6, 1),
            reference_period_start=date(2026, 6, 1),
            now=date(2026, 7, 10),
        )

    def test_vintage_sane_allows_publication_exactly_today(self):
        dq.assert_vintage_sane(
            publication_date=date(2026, 7, 10),
            reference_period_start=date(2026, 6, 1),
            now=date(2026, 7, 10),
        )

    def test_effective_not_future_accepts_past_or_today(self):
        dq.assert_effective_not_future(date(2026, 7, 1), now=date(2026, 7, 10))
        dq.assert_effective_not_future(date(2026, 7, 10), now=date(2026, 7, 10))

    def test_effective_not_future_rejects_future_date(self):
        with pytest.raises(ValueError, match="future"):
            dq.assert_effective_not_future(date(2026, 8, 1), now=date(2026, 7, 10))

    def test_functions_use_real_today_when_now_not_provided(self):
        """Smoke test that omitting `now` doesn't crash -- exercises the
        date.today() default path (can't assert an exact value since it's
        real wall-clock time, just that a clearly-past date does not raise)."""
        dq.assert_effective_not_future(date(2000, 1, 1))
        dq.assert_vintage_sane(
            publication_date=date(2000, 1, 2),
            reference_period_start=date(2000, 1, 1),
        )
