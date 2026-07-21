"""R2 Step 2 (DEC -> PriceObservations mirror, ClickUp 86cajtx2k) tests.

Coverage:
  TestMarketResolution        -- fail-closed raise when the Dambulla Markets
                                  row is missing (mirrors harti/loader.py's
                                  _dambulla_market_id contract).
  TestSqlTextContract          -- the eligibility/anti-join WHERE clause pins
                                  (non-positive rejected, NULL CropId
                                  rejected, id-keyed anti-join present, no
                                  UPDATE branch exists anywhere in the module).
  TestDryRunReport             -- counters shape, dry_run never writes.
  TestMirrorIdempotency        -- second call with would_insert=0 performs NO
                                  INSERT (engine.begin() never entered).
  TestMirrorInsertPath         -- INSERT executes with the right params when
                                  would_insert > 0; inserted == rowcount.
  TestPassionDualExtIdByDesign -- two DEC ExternalProductIds mapping to the
                                  SAME CropId is NOT collapsed/deduped by this
                                  module's SQL (both survive the anti-join
                                  independently) -- pinned via the SQL text
                                  (the NOT EXISTS clause keys on
                                  ExternalCommodityId, not CropId).

All hermetic: MagicMock engine, no live DB / network.
"""
from __future__ import annotations

import sys
import uuid
from datetime import date
from pathlib import Path
from unittest.mock import MagicMock

import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import dec_mirror as M  # noqa: E402


DAMBULLA_ID = uuid.UUID("b2a20001-0000-0000-0000-000000000001")


# ===========================================================================
# Mock engine helper: sequenced connect()-scoped executes (scalar/fetchone),
# plus an independent begin()-scoped execute (the INSERT) with a rowcount.
# ===========================================================================

def _mock_engine(connect_payloads: list, *, insert_rowcount: "int | None" = None) -> MagicMock:
    """`connect_payloads` is a list of dicts, one per successive
    `engine.connect()...execute()` call, each shaped like
    {"scalar": v} or {"fetchone": tuple}. `engine.begin()`'s single execute()
    (the INSERT) returns a result whose `.rowcount` is `insert_rowcount`.
    """
    engine = MagicMock()
    conn = MagicMock()
    state = {"n": 0}

    def _connect_execute(*_a, **_k):
        idx = state["n"]
        state["n"] += 1
        res = MagicMock()
        payload = connect_payloads[idx] if idx < len(connect_payloads) else {}
        res.scalar.return_value = payload.get("scalar")
        res.fetchone.return_value = payload.get("fetchone")
        res.fetchall.return_value = payload.get("fetchall", [])
        return res

    conn.execute.side_effect = _connect_execute
    engine.connect.return_value.__enter__.return_value = conn

    begin_conn = MagicMock()
    insert_result = MagicMock()
    insert_result.rowcount = insert_rowcount
    begin_conn.execute.return_value = insert_result
    engine.begin.return_value.__enter__.return_value = begin_conn

    return engine, conn, begin_conn


def _resolve_payload(market_id=DAMBULLA_ID):
    return {"fetchone": (str(market_id),)}


def _dry_run_payloads(*, eligible, would_insert, non_positive, null_crop, null_extid=0,
                       min_date="2025-05-05", max_date="2026-07-10", distinct_crops=95):
    """The 7 sequential connect-execute payloads dry_run_report() issues, in
    call order: resolve_market_id_by_code, eligible_total, would_insert,
    skipped_non_positive, skipped_null_crop, skipped_null_extid, date_row."""
    return [
        _resolve_payload(),
        {"scalar": eligible},
        {"scalar": would_insert},
        {"scalar": non_positive},
        {"scalar": null_crop},
        {"scalar": null_extid},
        {"fetchone": (date.fromisoformat(min_date), date.fromisoformat(max_date), distinct_crops)},
    ]


# ===========================================================================
# 1. Market resolution -- fail-closed
# ===========================================================================

class TestMarketResolution:
    def test_missing_dambulla_market_row_raises(self):
        engine, conn, _ = _mock_engine([{"fetchone": None}])
        with pytest.raises(RuntimeError, match="MKT00000001"):
            M.dry_run_report(engine)

    def test_market_id_resolved_by_code_not_hardcoded(self):
        """The resolver query must filter on MarketCode -- never assume a
        fixed GUID (GUIDs are per-DB)."""
        engine, conn, _ = _mock_engine(_dry_run_payloads(
            eligible=10, would_insert=10, non_positive=0, null_crop=0))
        M.dry_run_report(engine)
        first_call_sql = str(conn.execute.call_args_list[0][0][0])
        assert "MarketCode" in first_call_sql
        first_call_params = conn.execute.call_args_list[0][0][1]
        assert first_call_params == {"code": M.DAMBULLA_MARKET_CODE}


# ===========================================================================
# 2. SQL text pins
# ===========================================================================

class TestSqlTextContract:
    def test_eligible_where_rejects_non_positive_and_null_crop(self):
        assert "mp.MinPrice > 0" in M._ELIGIBLE_WHERE
        assert "mp.MaxPrice > 0" in M._ELIGIBLE_WHERE
        assert "mp.CropId IS NOT NULL" in M._ELIGIBLE_WHERE

    def test_eligible_where_rejects_null_external_product_id(self):
        """SF1 (reviewer should-fix): a NULL ExternalProductId must never
        reach the id-keyed anti-join -- NULL = NULL is never TRUE in SQL, so
        such a row would never match on re-run, land in the NAME-keyed
        unique index on INSERT, and the second run's unique-constraint
        violation would roll back the whole daily INSERT. See module
        docstring "NULL ExternalProductId GUARD"."""
        assert "mp.ExternalProductId IS NOT NULL" in M._ELIGIBLE_WHERE

    def test_not_exists_anti_join_keys_on_id_keyed_unique_index_columns(self):
        """The anti-join must match PriceObservations' id-keyed filtered
        unique index (MarketId, ExternalCommodityId, ObservedDate, Source) --
        NOT CropId (Passion's two ExternalProductIds sharing one CropId must
        both survive independently)."""
        not_exists = M._NOT_EXISTS
        assert "po.MarketId = :market_id" in not_exists
        assert "po.ExternalCommodityId = mp.ExternalProductId" in not_exists
        assert "po.ObservedDate = mp.PriceDate" in not_exists
        assert "po.Source = :dec_source" in not_exists
        assert "CropId" not in not_exists  # anti-join never keys on CropId

    def test_module_has_no_update_statement(self):
        """Insert-only by design (MarketPrices DEC rows are write-once at the
        source -- see module docstring). A regression that adds an UPDATE
        branch here would need a deliberate contract change, not a silent
        addition."""
        import inspect
        src = inspect.getsource(M)
        assert "UPDATE PriceObservations" not in src

    def test_insert_uses_asof_formula_not_retrievedatutc(self):
        """AsOfUtc must be computed from ObservedDate (+1 day, 00:30 UTC),
        never read from mp.RetrievedAtUtc (proven unreliable -- see module
        docstring)."""
        import inspect
        # Strip comment lines (which deliberately explain the NON-use of
        # mp.RetrievedAtUtc in prose) so this pins actual SQL usage only.
        src = inspect.getsource(M.mirror_dec_to_price_observations)
        live_lines = "\n".join(
            line for line in src.splitlines() if not line.strip().startswith("--")
        )
        assert "mp.RetrievedAtUtc" not in live_lines
        assert "DATEADD(DAY, 1, mp.PriceDate)" in src


# ===========================================================================
# 3. dry_run_report()
# ===========================================================================

class TestDryRunReport:
    def test_report_shape_and_arithmetic(self):
        engine, conn, begin_conn = _mock_engine(_dry_run_payloads(
            eligible=33620, would_insert=33620, non_positive=196, null_crop=0, null_extid=0))
        report = M.dry_run_report(engine)
        assert report["market_id"] == str(DAMBULLA_ID)
        assert report["eligible_total"] == 33620
        assert report["would_insert"] == 33620
        assert report["already_mirrored"] == 0
        assert report["skipped_non_positive"] == 196
        assert report["skipped_null_crop"] == 0
        assert report["skipped_null_extid"] == 0
        assert report["distinct_crops"] == 95
        assert report["min_observed_date"] == "2025-05-05"
        assert report["max_observed_date"] == "2026-07-10"
        # begin() must never be touched by a pure report call.
        begin_conn.execute.assert_not_called()

    def test_already_mirrored_is_eligible_minus_would_insert(self):
        engine, conn, _ = _mock_engine(_dry_run_payloads(
            eligible=1000, would_insert=40, non_positive=0, null_crop=0))
        report = M.dry_run_report(engine)
        assert report["already_mirrored"] == 960

    def test_dry_run_flag_on_mirror_call_never_writes(self):
        engine, conn, begin_conn = _mock_engine(_dry_run_payloads(
            eligible=100, would_insert=100, non_positive=0, null_crop=0))
        result = M.mirror_dec_to_price_observations(engine, dry_run=True)
        assert result["inserted"] == 0
        begin_conn.execute.assert_not_called()

    def test_null_extid_rows_are_counted_and_excluded_from_eligible(self):
        """SF1: a NULL-ExternalProductId DEC row must be surfaced via
        skipped_null_extid, not silently absorbed into skipped_non_positive
        or skipped_null_crop (distinct failure mode, distinct counter)."""
        engine, conn, _ = _mock_engine(_dry_run_payloads(
            eligible=33620, would_insert=33620, non_positive=196,
            null_crop=0, null_extid=3))
        report = M.dry_run_report(engine)
        assert report["skipped_null_extid"] == 3
        # Call order (0-based): 0=resolve, 1=eligible_total, 2=would_insert,
        # 3=skipped_non_positive, 4=skipped_null_crop, 5=skipped_null_extid,
        # 6=date_row -- confirm null_crop and null_extid are issued as
        # SEPARATE queries, not one query double-counted.
        fifth_call_sql = str(conn.execute.call_args_list[4][0][0])
        sixth_call_sql = str(conn.execute.call_args_list[5][0][0])
        assert "mp.CropId IS NULL" in fifth_call_sql
        assert "mp.ExternalProductId IS NULL" in sixth_call_sql


# ===========================================================================
# 4. Idempotency: nothing new -> no INSERT issued at all
# ===========================================================================

class TestMirrorIdempotency:
    def test_zero_would_insert_never_opens_a_write_transaction(self):
        engine, conn, begin_conn = _mock_engine(_dry_run_payloads(
            eligible=33620, would_insert=0, non_positive=196, null_crop=0))
        result = M.mirror_dec_to_price_observations(engine, dry_run=False)
        assert result["inserted"] == 0
        begin_conn.execute.assert_not_called()


# ===========================================================================
# 5. Insert path
# ===========================================================================

class TestMirrorInsertPath:
    def test_insert_executes_with_source_and_unit_params_and_reports_rowcount(self):
        engine, conn, begin_conn = _mock_engine(
            _dry_run_payloads(eligible=500, would_insert=500, non_positive=0, null_crop=0),
            insert_rowcount=500,
        )
        result = M.mirror_dec_to_price_observations(engine, dry_run=False)
        assert result["inserted"] == 500
        begin_conn.execute.assert_called_once()
        insert_sql = str(begin_conn.execute.call_args[0][0])
        insert_params = begin_conn.execute.call_args[0][1]
        assert "INSERT INTO PriceObservations" in insert_sql
        assert insert_params["dec_source"] == "DAMBULLA_DEC"
        assert insert_params["unit_raw"] == "Rs/kg"
        assert insert_params["unit_factor"] == 1.0
        assert insert_params["market_id"] == str(DAMBULLA_ID)

    def test_since_date_threaded_into_both_report_and_insert(self):
        engine, conn, begin_conn = _mock_engine(
            _dry_run_payloads(eligible=12, would_insert=12, non_positive=0, null_crop=0),
            insert_rowcount=12,
        )
        since = date(2026, 7, 10)
        M.mirror_dec_to_price_observations(engine, since_date=since, dry_run=False)
        insert_params = begin_conn.execute.call_args[0][1]
        assert insert_params["since_date"] == since
        insert_sql = str(begin_conn.execute.call_args[0][0])
        assert "mp.PriceDate > :since_date" in insert_sql

    def test_negative_rowcount_driver_falls_back_to_would_insert(self):
        """Some drivers report rowcount=-1 for certain statement shapes;
        fall back to the pre-computed would_insert count rather than
        reporting a nonsensical negative number."""
        engine, conn, begin_conn = _mock_engine(
            _dry_run_payloads(eligible=7, would_insert=7, non_positive=0, null_crop=0),
            insert_rowcount=-1,
        )
        result = M.mirror_dec_to_price_observations(engine, dry_run=False)
        assert result["inserted"] == 7


# ===========================================================================
# 6. Passion by-design duplicate: two ext-ids, one CropId
# ===========================================================================

class TestPassionDualExtIdByDesign:
    def test_anti_join_and_insert_never_dedupe_on_cropid(self):
        """Both statements (COUNT-eligibility and INSERT) must never filter
        or GROUP BY CropId in a way that would collapse Passion's two rows
        per date -- the id-keyed unique index (scoped on ExternalCommodityId)
        is what allows both to coexist, and this module must not add its own
        CropId-based collapsing on top."""
        assert "GROUP BY" not in M._ELIGIBLE_WHERE
        assert "DISTINCT mp.CropId" not in M._NOT_EXISTS
        import inspect
        insert_src = inspect.getsource(M.mirror_dec_to_price_observations)
        assert "GROUP BY" not in insert_src
