"""Post-ingestion data verification tests (R2 ingestion-log plan PR 2).

All tests are hermetic: a mocked SQLAlchemy engine (MagicMock, matching the
style of test_dec_mirror.py's ``_mock_engine`` sequenced-payload pattern),
no network, no live DB. Live-DB verification is done separately (see task
report).

Coverage:
  TestDecRowCount               -- WARN/FAIL boundaries (78/96/20/115) plus
                                    the dedicated "0 rows" FAIL message.
  TestDecUnmapped                -- 0 -> PASS, >0 -> FAIL.
  TestNonPositivePricesScoping   -- the since_utc parameter is threaded
                                    verbatim into the SQL params, i.e. the
                                    "new rows only" 24h scoping is enforced
                                    by the query itself, not a Python-side
                                    date filter that could drift.
  TestMinGtMax / TestExtremeOutlier
                                  -- FAIL vs WARN severities respectively.
  TestAliasRegression             -- Garlic FAIL (wrong code / Big-Onion
                                    name), Passion WARN (drift), both-hold
                                    PASS.
  TestNaturalKeyDups              -- 0 groups -> PASS, any group -> FAIL.
  TestHartiConvention             -- violation -> FAIL, clean -> PASS.
  TestHartiFreshness              -- PASS<=2, WARN 3-5, FAIL>5, no rows -> FAIL.
  TestMirrorCurrency              -- match -> PASS; mismatch + no prior row
                                    -> WARN; mismatch + prior row also
                                    mismatched -> FAIL; malformed prior
                                    ChecksJson treated as not-previously-
                                    mismatched (fail-open to WARN).
  TestMirrorIdempotency           -- would_insert 0 -> PASS, >0 -> FAIL.
  TestMirrorFidelity              -- 0 mismatches -> PASS; fraction<=1% ->
                                    WARN; fraction>1% -> FAIL; empty sample
                                    -> PASS.
  TestCrossSourceDups             -- AssertionError -> FAIL, clean -> PASS.
  TestGapTiers                    -- ERROR-tier entries in window -> WARN
                                    (never FAIL); outside window -> PASS.
  TestSeverityAggregation         -- run_all_verifications' overall = worst
                                    of the 14 checks; n_pass/n_warn/n_fail
                                    counts; pipeline_date/duration_ms shape.
  TestChecksJsonRoundTrip         -- json.dumps(checks) round-trips via
                                    json.loads to the identical structure.
  TestSummarySanitization         -- Summary never contains a check's full
                                    message text (path-safe by construction).
  TestPersistVerdict              -- INSERT SQL/params shape, OverallStatus
                                    int mapping, BatchId nullable.
  TestCliExitCodes                 -- 0 PASS/WARN, 1 FAIL, 2 infra error
                                    (run failure OR persist failure), 2 on
                                    bad --pipeline-date, --dry-run skips
                                    persistence.
"""
from __future__ import annotations

import json
import logging
import sys
import uuid
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import MagicMock

import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import ingest_verify as iv  # noqa: E402
from agriforecast_ml import data_quality as dq  # noqa: E402
import verify_ingestion  # noqa: E402


# ===========================================================================
# Mock engine helper (mirrors test_dec_mirror.py::_mock_engine)
# ===========================================================================

def _mock_engine(connect_payloads: list) -> "tuple[MagicMock, MagicMock]":
    """`connect_payloads` is a list of dicts, one per successive
    `engine.connect()...execute()` call (in call order), each shaped like
    {"scalar": v}, {"fetchone": tuple}, or {"fetchall": [tuples]}.
    """
    engine = MagicMock()
    conn = MagicMock()
    state = {"n": 0}
    calls: list = []

    def _connect_execute(stmt, params=None):
        idx = state["n"]
        state["n"] += 1
        calls.append((str(stmt), params))
        res = MagicMock()
        payload = connect_payloads[idx] if idx < len(connect_payloads) else {}
        res.scalar.return_value = payload.get("scalar")
        res.fetchone.return_value = payload.get("fetchone")
        res.fetchall.return_value = payload.get("fetchall", [])
        return res

    conn.execute.side_effect = _connect_execute
    conn.execute.call_log = calls  # convenience for tests inspecting SQL/params
    engine.connect.return_value.__enter__.return_value = conn
    engine.begin.return_value.__enter__.return_value = conn
    return engine, conn


# ===========================================================================
# 1. dec_row_count
# ===========================================================================

class TestDecRowCount:
    PD = date(2026, 7, 21)

    @pytest.mark.parametrize("count,expected", [
        (0, "FAIL"),
        (19, "FAIL"),
        (20, "WARN"),
        (77, "WARN"),
        (78, "PASS"),
        (96, "PASS"),
        (97, "WARN"),
        (115, "WARN"),
        (116, "FAIL"),
    ])
    def test_boundaries(self, count, expected):
        engine, conn = _mock_engine([{"scalar": count}])
        result = iv._check_dec_row_count(engine, self.PD)
        assert result.severity == expected
        assert result.counts["row_count"] == count

    def test_zero_rows_gets_dedicated_message(self):
        engine, conn = _mock_engine([{"scalar": 0}])
        result = iv._check_dec_row_count(engine, self.PD)
        assert "has not run yet" in result.message or "feed" in result.message.lower()


# ===========================================================================
# 2. dec_unmapped
# ===========================================================================

class TestDecUnmapped:
    def test_zero_unmapped_passes(self):
        engine, conn = _mock_engine([{"scalar": 0}])
        result = iv._check_dec_unmapped(engine, date(2026, 7, 21))
        assert result.severity == "PASS"

    def test_any_unmapped_fails(self):
        engine, conn = _mock_engine([{"scalar": 3}])
        result = iv._check_dec_unmapped(engine, date(2026, 7, 21))
        assert result.severity == "FAIL"
        assert result.counts["unmapped_count"] == 3


# ===========================================================================
# 3. non_positive_prices -- "new rows only" scoping
# ===========================================================================

class TestNonPositivePricesScoping:
    def test_since_utc_threaded_into_both_queries(self):
        since = datetime(2026, 7, 20, 12, 0, tzinfo=timezone.utc)
        engine, conn = _mock_engine([{"scalar": 0}, {"scalar": 0}])
        iv._check_non_positive_prices(engine, since)
        assert conn.execute.call_log[0][1]["since"] == since
        assert conn.execute.call_log[1][1]["since"] == since
        assert "RetrievedAtUtc >= :since" in conn.execute.call_log[0][0]
        assert "RetrievedAtUtc >= :since" in conn.execute.call_log[1][0]

    def test_zero_both_tables_passes(self):
        engine, conn = _mock_engine([{"scalar": 0}, {"scalar": 0}])
        result = iv._check_non_positive_prices(engine, datetime.now(timezone.utc))
        assert result.severity == "PASS"

    def test_any_non_positive_fails(self):
        engine, conn = _mock_engine([{"scalar": 2}, {"scalar": 1}])
        result = iv._check_non_positive_prices(engine, datetime.now(timezone.utc))
        assert result.severity == "FAIL"
        assert result.counts == {"market_prices": 2, "price_observations": 1, "total": 3}

    def test_run_all_verifications_computes_since_as_roughly_24h_ago(self, monkeypatch):
        """Integration-level pin: run_all_verifications must compute a single
        `since_24h` ~24h before "now" and pass it to every last-24h-scoped
        check -- not recompute "now" separately per check (which could
        create a few-millisecond window drift between checks)."""
        seen_sinces = []

        def _fake_non_positive(engine, since_utc):
            seen_sinces.append(since_utc)
            return iv.CheckResult("non_positive_prices", "PASS", "ok")

        def _fake_min_gt_max(engine, since_utc):
            seen_sinces.append(since_utc)
            return iv.CheckResult("min_gt_max", "PASS", "ok")

        _patch_all_checks_to_pass(monkeypatch, overrides={
            "_check_non_positive_prices": _fake_non_positive,
            "_check_min_gt_max": _fake_min_gt_max,
        })

        iv.run_all_verifications(MagicMock(), pipeline_date=date(2026, 7, 21))
        assert len(seen_sinces) == 2
        assert seen_sinces[0] == seen_sinces[1]
        now = datetime.now(timezone.utc)
        delta = now - seen_sinces[0]
        assert timedelta(hours=23, minutes=59) < delta < timedelta(hours=24, minutes=1)


# ===========================================================================
# 4/5. min_gt_max / extreme_outlier
# ===========================================================================

class TestMinGtMax:
    def test_fails_on_any(self):
        engine, conn = _mock_engine([{"scalar": 1}, {"scalar": 0}])
        result = iv._check_min_gt_max(engine, datetime.now(timezone.utc))
        assert result.severity == "FAIL"

    def test_passes_clean(self):
        engine, conn = _mock_engine([{"scalar": 0}, {"scalar": 0}])
        result = iv._check_min_gt_max(engine, datetime.now(timezone.utc))
        assert result.severity == "PASS"


class TestExtremeOutlier:
    def test_warns_only_never_fails(self):
        engine, conn = _mock_engine([{"scalar": 5}, {"scalar": 0}])
        result = iv._check_extreme_outlier(engine, datetime.now(timezone.utc))
        assert result.severity == "WARN"

    def test_passes_clean(self):
        engine, conn = _mock_engine([{"scalar": 0}, {"scalar": 0}])
        result = iv._check_extreme_outlier(engine, datetime.now(timezone.utc))
        assert result.severity == "PASS"


# ===========================================================================
# 6. alias_regression
# ===========================================================================

class TestAliasRegression:
    def _rows(self, garlic, passion):
        return _mock_engine([{"fetchall": garlic}, {"fetchall": passion}])

    def test_both_correct_passes(self):
        engine, conn = self._rows(
            garlic=[("VEG000071", "Garlic")],
            passion=[(43, "FRT000019"), (76, "FRT000019")],
        )
        result = iv._check_alias_regression(engine)
        assert result.severity == "PASS"

    def test_garlic_wrong_code_fails(self):
        engine, conn = self._rows(
            garlic=[("VEG000099", "Big Onion")],
            passion=[(43, "FRT000019"), (76, "FRT000019")],
        )
        result = iv._check_alias_regression(engine)
        assert result.severity == "FAIL"

    def test_garlic_no_mapping_rows_at_all_gets_dedicated_message(self):
        """Nit fix: an empty result set (ext-47 has no rows at all) must
        read as a dedicated "no mapping rows found" message, not the
        generic "maps to CropCode(s) []" wording."""
        engine, conn = self._rows(
            garlic=[],
            passion=[(43, "FRT000019"), (76, "FRT000019")],
        )
        result = iv._check_alias_regression(engine)
        assert result.severity == "FAIL"
        assert result.message == "no ext-47 mapping rows found (expected VEG000071)"
        assert "CropCode(s)" not in result.message
        assert "<none>" not in result.message

    def test_garlic_big_onion_name_fails_even_if_code_matches(self):
        engine, conn = self._rows(
            garlic=[("VEG000071", "Big Onion (mislabelled)")],
            passion=[(43, "FRT000019"), (76, "FRT000019")],
        )
        result = iv._check_alias_regression(engine)
        assert result.severity == "FAIL"

    def test_passion_drift_warns_not_fails(self):
        engine, conn = self._rows(
            garlic=[("VEG000071", "Garlic")],
            passion=[(43, "FRT000019"), (76, "FRT000020")],  # drifted
        )
        result = iv._check_alias_regression(engine)
        assert result.severity == "WARN"

    def test_passion_missing_ext_id_warns(self):
        engine, conn = self._rows(
            garlic=[("VEG000071", "Garlic")],
            passion=[(43, "FRT000019")],  # 76 missing
        )
        result = iv._check_alias_regression(engine)
        assert result.severity == "WARN"


# ===========================================================================
# 7. natural_key_dups
# ===========================================================================

class TestNaturalKeyDups:
    def test_all_zero_passes(self):
        engine, conn = _mock_engine([{"scalar": 0}, {"scalar": 0}, {"scalar": 0}])
        result = iv._check_natural_key_dups(engine)
        assert result.severity == "PASS"

    def test_any_nonzero_fails(self):
        engine, conn = _mock_engine([{"scalar": 0}, {"scalar": 2}, {"scalar": 0}])
        result = iv._check_natural_key_dups(engine)
        assert result.severity == "FAIL"
        assert result.counts["po_name_keyed_dup_groups"] == 2


# ===========================================================================
# 8. harti_convention
# ===========================================================================

class TestHartiConvention:
    def test_violation_fails(self):
        engine, conn = _mock_engine([{"scalar": 10}, {"scalar": 2}])
        result = iv._check_harti_convention(engine, datetime.now(timezone.utc))
        assert result.severity == "FAIL"

    def test_clean_passes(self):
        engine, conn = _mock_engine([{"scalar": 10}, {"scalar": 0}])
        result = iv._check_harti_convention(engine, datetime.now(timezone.utc))
        assert result.severity == "PASS"

    def test_zero_new_rows_still_passes(self):
        engine, conn = _mock_engine([{"scalar": 0}, {"scalar": 0}])
        result = iv._check_harti_convention(engine, datetime.now(timezone.utc))
        assert result.severity == "PASS"


# ===========================================================================
# 9. harti_freshness
# ===========================================================================

class TestHartiFreshness:
    PD = date(2026, 7, 21)

    @pytest.mark.parametrize("days_stale,expected", [
        (0, "PASS"),
        (2, "PASS"),
        (3, "WARN"),
        (5, "WARN"),
        (6, "FAIL"),
    ])
    def test_boundaries(self, days_stale, expected):
        max_date = self.PD - timedelta(days=days_stale)
        engine, conn = _mock_engine([{"scalar": max_date}])
        result = iv._check_harti_freshness(engine, self.PD)
        assert result.severity == expected
        assert result.counts["days_stale"] == days_stale

    def test_no_harti_rows_fails(self):
        engine, conn = _mock_engine([{"scalar": None}])
        result = iv._check_harti_freshness(engine, self.PD)
        assert result.severity == "FAIL"


# ===========================================================================
# 10. mirror_currency
# ===========================================================================

class TestMirrorCurrency:
    def test_matching_dates_pass(self):
        d = date(2026, 7, 21)
        engine, conn = _mock_engine([{"scalar": d}, {"scalar": d}, {"fetchone": None}])
        result = iv._check_mirror_currency(engine)
        assert result.severity == "PASS"

    def test_mismatch_no_prior_row_warns(self):
        engine, conn = _mock_engine([
            {"scalar": date(2026, 7, 21)},
            {"scalar": date(2026, 7, 20)},
            {"fetchone": None},
        ])
        result = iv._check_mirror_currency(engine)
        assert result.severity == "WARN"

    def test_mismatch_previously_mismatched_fails(self):
        prev_checks = json.dumps([
            {"name": "mirror_currency", "severity": "WARN", "message": "x", "counts": {}},
        ])
        engine, conn = _mock_engine([
            {"scalar": date(2026, 7, 21)},
            {"scalar": date(2026, 7, 20)},
            {"fetchone": (prev_checks,)},
        ])
        result = iv._check_mirror_currency(engine)
        assert result.severity == "FAIL"
        assert "persistent" in result.message.lower()

    def test_mismatch_previously_passed_warns_not_fails(self):
        prev_checks = json.dumps([
            {"name": "mirror_currency", "severity": "PASS", "message": "x", "counts": {}},
        ])
        engine, conn = _mock_engine([
            {"scalar": date(2026, 7, 21)},
            {"scalar": date(2026, 7, 20)},
            {"fetchone": (prev_checks,)},
        ])
        result = iv._check_mirror_currency(engine)
        assert result.severity == "WARN"

    def test_malformed_prior_json_fails_open_to_warn(self):
        engine, conn = _mock_engine([
            {"scalar": date(2026, 7, 21)},
            {"scalar": date(2026, 7, 20)},
            {"fetchone": ("{not valid json",)},
        ])
        result = iv._check_mirror_currency(engine)
        assert result.severity == "WARN"


# ===========================================================================
# 11. mirror_idempotency
# ===========================================================================

class TestMirrorIdempotency:
    def test_zero_would_insert_passes(self, monkeypatch):
        monkeypatch.setattr(iv.dec_mirror, "dry_run_report", lambda engine: {"would_insert": 0})
        result = iv._check_mirror_idempotency(MagicMock())
        assert result.severity == "PASS"

    def test_nonzero_would_insert_fails(self, monkeypatch):
        monkeypatch.setattr(iv.dec_mirror, "dry_run_report", lambda engine: {"would_insert": 12})
        result = iv._check_mirror_idempotency(MagicMock())
        assert result.severity == "FAIL"
        assert result.counts["would_insert"] == 12


# ===========================================================================
# 12. mirror_fidelity
# ===========================================================================

class TestMirrorFidelity:
    def _row(self, po_min, po_max, po_crop, mp_min, mp_max, mp_crop):
        return (po_min, po_max, po_crop, mp_min, mp_max, mp_crop)

    def test_empty_sample_passes(self):
        engine, conn = _mock_engine([{"fetchall": []}])
        result = iv._check_mirror_fidelity(engine)
        assert result.severity == "PASS"
        assert result.counts["sample_size"] == 0

    def test_zero_mismatches_passes(self):
        rows = [self._row(10, 20, "c1", 10, 20, "c1") for _ in range(50)]
        engine, conn = _mock_engine([{"fetchall": rows}])
        result = iv._check_mirror_fidelity(engine)
        assert result.severity == "PASS"

    def test_small_fraction_warns(self):
        # 1/150 = 0.67% <= 1% -> WARN (see module docstring: unreachable at
        # the real n=50 cap, but the arithmetic itself must be correct for
        # any sample size the query could ever return).
        rows = [self._row(10, 20, "c1", 10, 20, "c1") for _ in range(149)]
        rows.append(self._row(10, 20, "c1", 999, 20, "c1"))
        engine, conn = _mock_engine([{"fetchall": rows}])
        result = iv._check_mirror_fidelity(engine)
        assert result.severity == "WARN"

    def test_large_fraction_fails(self):
        # 2/150 = 1.33% > 1% -> FAIL
        rows = [self._row(10, 20, "c1", 10, 20, "c1") for _ in range(148)]
        rows.append(self._row(10, 20, "c1", 999, 20, "c1"))
        rows.append(self._row(10, 20, "c1", 20, 999, "c1"))
        engine, conn = _mock_engine([{"fetchall": rows}])
        result = iv._check_mirror_fidelity(engine)
        assert result.severity == "FAIL"

    def test_at_n50_any_mismatch_fails(self):
        """Pins the documented n=50 behaviour: at the real, pinned sample
        size, a single mismatch already exceeds the 1% threshold."""
        rows = [self._row(10, 20, "c1", 10, 20, "c1") for _ in range(49)]
        rows.append(self._row(10, 20, "c1", 999, 20, "c1"))
        engine, conn = _mock_engine([{"fetchall": rows}])
        result = iv._check_mirror_fidelity(engine)
        assert result.severity == "FAIL"

    def test_crop_id_mismatch_detected(self):
        rows = [self._row(10, 20, "c1", 10, 20, "c2")]
        engine, conn = _mock_engine([{"fetchall": rows}])
        result = iv._check_mirror_fidelity(engine)
        assert result.counts["mismatches"] == 1


# ===========================================================================
# 13. cross_source_dups
# ===========================================================================

class TestCrossSourceDups:
    def test_clean_passes(self, monkeypatch):
        monkeypatch.setattr(iv.dq, "assert_no_source_duplicates", lambda engine: 12345)
        result = iv._check_cross_source_dups(MagicMock())
        assert result.severity == "PASS"
        assert result.counts["triples_examined"] == 12345

    def test_violation_fails(self, monkeypatch):
        def _raise(engine):
            raise AssertionError("SOURCE DUPLICATION: 3 triples ...")
        monkeypatch.setattr(iv.dq, "assert_no_source_duplicates", _raise)
        result = iv._check_cross_source_dups(MagicMock())
        assert result.severity == "FAIL"
        assert "SOURCE DUPLICATION" in result.message


# ===========================================================================
# 14. gap_tiers
# ===========================================================================

class TestGapTiers:
    PD = date(2026, 7, 21)

    def test_no_entries_passes(self, monkeypatch):
        monkeypatch.setattr(iv.dq, "gap_report", lambda engine, source: {
            "entries": [], "series_scanned": 10,
        })
        result = iv._check_gap_tiers(MagicMock(), self.PD)
        assert result.severity == "PASS"

    def test_recent_error_entry_warns_never_fails(self, monkeypatch):
        recent_end = (self.PD - timedelta(days=5)).isoformat()
        monkeypatch.setattr(iv.dq, "gap_report", lambda engine, source: {
            "entries": [{"tier": "ERROR", "gap_end": recent_end, "gap_start": "2026-06-01"}],
            "series_scanned": 10,
        })
        result = iv._check_gap_tiers(MagicMock(), self.PD)
        assert result.severity == "WARN"

    def test_old_error_entry_outside_window_passes(self, monkeypatch):
        old_end = (self.PD - timedelta(days=iv.GAP_TIER_RECENT_WINDOW_DAYS + 30)).isoformat()
        monkeypatch.setattr(iv.dq, "gap_report", lambda engine, source: {
            "entries": [{"tier": "ERROR", "gap_end": old_end, "gap_start": "2020-01-01"}],
            "series_scanned": 10,
        })
        result = iv._check_gap_tiers(MagicMock(), self.PD)
        assert result.severity == "PASS"

    def test_warning_tier_entries_are_ignored(self, monkeypatch):
        recent_end = (self.PD - timedelta(days=1)).isoformat()
        monkeypatch.setattr(iv.dq, "gap_report", lambda engine, source: {
            "entries": [{"tier": "WARNING", "gap_end": recent_end, "gap_start": "2026-07-01"}],
            "series_scanned": 10,
        })
        result = iv._check_gap_tiers(MagicMock(), self.PD)
        assert result.severity == "PASS"


# ===========================================================================
# Orchestration: severity aggregation, shape, ChecksJson round-trip, Summary
# ===========================================================================

_ALL_CHECK_FN_NAMES = [
    "_check_dec_row_count", "_check_dec_unmapped", "_check_non_positive_prices",
    "_check_min_gt_max", "_check_extreme_outlier", "_check_alias_regression",
    "_check_natural_key_dups", "_check_harti_convention", "_check_harti_freshness",
    "_check_mirror_currency", "_check_mirror_idempotency", "_check_mirror_fidelity",
    "_check_cross_source_dups", "_check_gap_tiers",
]


def _patch_all_checks_to_pass(monkeypatch, overrides: "dict | None" = None):
    """Monkeypatch every module-level _check_* function to a trivial PASS
    stub (arity-flexible via *args), except any name present in `overrides`
    (used to inject a specific severity/behaviour for one check under test).
    This decouples orchestration-logic tests (severity aggregation, shape,
    duration/pipeline_date plumbing) entirely from each check's own SQL --
    those are covered individually above."""
    overrides = overrides or {}
    for name in _ALL_CHECK_FN_NAMES:
        if name in overrides:
            monkeypatch.setattr(iv, name, overrides[name])
        else:
            short = name.replace("_check_", "")
            monkeypatch.setattr(iv, name, lambda *a, _short=short, **k: iv.CheckResult(_short, "PASS", "ok"))


class TestSeverityAggregation:
    def test_all_pass_overall_pass(self, monkeypatch):
        _patch_all_checks_to_pass(monkeypatch)
        result = iv.run_all_verifications(MagicMock(), pipeline_date=date(2026, 7, 21))
        assert result["overall"] == "PASS"
        assert result["n_pass"] == 14
        assert result["n_warn"] == 0
        assert result["n_fail"] == 0
        assert len(result["checks"]) == 14

    def test_one_warn_overall_warn(self, monkeypatch):
        _patch_all_checks_to_pass(monkeypatch, overrides={
            "_check_extreme_outlier": lambda *a, **k: iv.CheckResult("extreme_outlier", "WARN", "x"),
        })
        result = iv.run_all_verifications(MagicMock(), pipeline_date=date(2026, 7, 21))
        assert result["overall"] == "WARN"
        assert result["n_pass"] == 13
        assert result["n_warn"] == 1
        assert result["n_fail"] == 0

    def test_warn_and_fail_overall_fail(self, monkeypatch):
        _patch_all_checks_to_pass(monkeypatch, overrides={
            "_check_extreme_outlier": lambda *a, **k: iv.CheckResult("extreme_outlier", "WARN", "x"),
            "_check_dec_unmapped": lambda *a, **k: iv.CheckResult("dec_unmapped", "FAIL", "y"),
        })
        result = iv.run_all_verifications(MagicMock(), pipeline_date=date(2026, 7, 21))
        assert result["overall"] == "FAIL"
        assert result["n_pass"] == 12
        assert result["n_warn"] == 1
        assert result["n_fail"] == 1

    def test_pipeline_date_defaults_to_today_in_utc_not_local(self, monkeypatch):
        """S1 review fix: the default must be UTC "today", not
        date.today() (local calendar date) -- pin against a fixed,
        injected "now" so this test is correct regardless of the
        machine's local timezone/clock at test-run time."""
        fixed_now = datetime(2026, 7, 21, 23, 30, tzinfo=timezone.utc)

        class _FixedDatetime(datetime):
            @classmethod
            def now(cls, tz=None):
                return fixed_now if tz is not None else fixed_now.replace(tzinfo=None)

        _patch_all_checks_to_pass(monkeypatch)
        monkeypatch.setattr(iv, "datetime", _FixedDatetime)
        result = iv.run_all_verifications(MagicMock())
        assert result["pipeline_date"] == "2026-07-21"

    def test_pipeline_date_default_uses_utc_date_helper(self, monkeypatch):
        """Cheaper companion pin (no datetime subclassing): the real,
        un-mocked default must equal datetime.now(timezone.utc).date(),
        not date.today() -- these two can legitimately differ near UTC
        midnight, which is exactly the bug this fix closes."""
        _patch_all_checks_to_pass(monkeypatch)
        result = iv.run_all_verifications(MagicMock())
        assert result["pipeline_date"] == datetime.now(timezone.utc).date().isoformat()

    def test_pipeline_date_explicit_is_used(self, monkeypatch):
        _patch_all_checks_to_pass(monkeypatch)
        result = iv.run_all_verifications(MagicMock(), pipeline_date=date(2020, 1, 1))
        assert result["pipeline_date"] == "2020-01-01"

    def test_duration_ms_is_nonnegative_int(self, monkeypatch):
        _patch_all_checks_to_pass(monkeypatch)
        result = iv.run_all_verifications(MagicMock(), pipeline_date=date(2026, 7, 21))
        assert isinstance(result["duration_ms"], int)
        assert result["duration_ms"] >= 0

    def test_batch_id_not_leaked_into_result_shape(self, monkeypatch):
        _patch_all_checks_to_pass(monkeypatch)
        result = iv.run_all_verifications(
            MagicMock(), pipeline_date=date(2026, 7, 21), batch_id=uuid.uuid4(),
        )
        assert set(result.keys()) == {
            "overall", "checks", "n_pass", "n_warn", "n_fail", "pipeline_date", "duration_ms",
        }


class TestChecksJsonRoundTrip:
    def test_round_trips_identically(self, monkeypatch):
        _patch_all_checks_to_pass(monkeypatch, overrides={
            "_check_natural_key_dups": lambda *a, **k: iv.CheckResult(
                "natural_key_dups", "FAIL", "3 dup groups",
                {"po_id_keyed_dup_groups": 3, "po_name_keyed_dup_groups": 0, "market_prices_dup_groups": 0},
            ),
        })
        result = iv.run_all_verifications(MagicMock(), pipeline_date=date(2026, 7, 21))
        checks_json = json.dumps(result["checks"])
        round_tripped = json.loads(checks_json)
        assert round_tripped == result["checks"]
        assert len(round_tripped) == 14
        assert all({"name", "severity", "message", "counts"} <= set(c.keys()) for c in round_tripped)


class TestSummarySanitization:
    def test_summary_excludes_full_check_messages(self, monkeypatch):
        _patch_all_checks_to_pass(monkeypatch, overrides={
            "_check_cross_source_dups": lambda *a, **k: iv.CheckResult(
                "cross_source_dups", "FAIL",
                "SOURCE DUPLICATION at /some/suspicious/path/secret.txt with Traceback (most recent call last)",
            ),
        })
        result = iv.run_all_verifications(MagicMock(), pipeline_date=date(2026, 7, 21))
        summary = iv._build_summary(result)
        assert "/some/suspicious/path" not in summary
        assert "Traceback" not in summary
        assert "cross_source_dups" in summary  # check NAME is fine, message text is not

    def test_summary_capped_at_1000_chars(self, monkeypatch):
        _patch_all_checks_to_pass(monkeypatch)
        result = iv.run_all_verifications(MagicMock(), pipeline_date=date(2026, 7, 21))
        assert len(iv._build_summary(result)) <= 1000

    def test_summary_reflects_overall_and_counts(self, monkeypatch):
        _patch_all_checks_to_pass(monkeypatch, overrides={
            "_check_extreme_outlier": lambda *a, **k: iv.CheckResult("extreme_outlier", "WARN", "x"),
        })
        result = iv.run_all_verifications(MagicMock(), pipeline_date=date(2026, 7, 21))
        summary = iv._build_summary(result)
        assert summary.startswith("WARN:")
        assert "13 pass" in summary and "1 warn" in summary and "0 fail" in summary


# ===========================================================================
# persist_verdict()
# ===========================================================================

class TestPersistVerdict:
    def _result(self, overall="PASS", n_pass=14, n_warn=0, n_fail=0):
        return {
            "overall": overall,
            "checks": [{"name": "dec_row_count", "severity": "PASS", "message": "ok", "counts": {}}],
            "n_pass": n_pass, "n_warn": n_warn, "n_fail": n_fail,
            "pipeline_date": "2026-07-21",
            "duration_ms": 42,
        }

    def test_insert_sql_and_params_shape(self):
        engine, conn = _mock_engine([])
        verdict_id = iv.persist_verdict(engine, self._result(), batch_id=None)
        assert isinstance(verdict_id, uuid.UUID)
        sql, params = conn.execute.call_log[0]
        assert "INSERT INTO IngestionVerifications" in sql
        assert params["batch_id"] is None
        assert params["pipeline_date"] == date(2026, 7, 21)
        assert params["overall_status"] == 0
        assert params["n_pass"] == 14
        assert json.loads(params["checks_json"]) == self._result()["checks"]
        assert params["duration_ms"] == 42

    def test_overall_status_mapping(self):
        for overall, expected_code in [("PASS", 0), ("WARN", 1), ("FAIL", 2)]:
            engine, conn = _mock_engine([])
            iv.persist_verdict(engine, self._result(overall=overall))
            _, params = conn.execute.call_log[0]
            assert params["overall_status"] == expected_code

    def test_batch_id_threaded_as_string(self):
        bid = uuid.uuid4()
        engine, conn = _mock_engine([])
        iv.persist_verdict(engine, self._result(), batch_id=bid)
        _, params = conn.execute.call_log[0]
        assert params["batch_id"] == str(bid)

    def test_result_none_raises(self):
        engine, conn = _mock_engine([])
        with pytest.raises(ValueError):
            iv.persist_verdict(engine, None)


# ===========================================================================
# CLI exit codes
# ===========================================================================

class TestCliExitCodes:
    def _patch_common(self, monkeypatch, *, run_result=None, run_raises=None,
                       persist_raises=None):
        monkeypatch.setattr(verify_ingestion, "load_env_file", lambda: None)
        monkeypatch.setattr(verify_ingestion, "get_engine", lambda: MagicMock())

        def _fake_run(engine, *, pipeline_date=None, batch_id=None):
            if run_raises is not None:
                raise run_raises
            return run_result

        monkeypatch.setattr(verify_ingestion.ingest_verify, "run_all_verifications", _fake_run)

        persisted = {"called": False}

        def _fake_persist(engine, result, *, batch_id=None):
            persisted["called"] = True
            if persist_raises is not None:
                raise persist_raises
            return uuid.uuid4()

        monkeypatch.setattr(verify_ingestion.ingest_verify, "persist_verdict", _fake_persist)
        return persisted

    def _result(self, overall):
        return {
            "overall": overall,
            "checks": [{"name": "x", "severity": overall, "message": "m", "counts": {}}],
            "n_pass": 1 if overall == "PASS" else 0,
            "n_warn": 1 if overall == "WARN" else 0,
            "n_fail": 1 if overall == "FAIL" else 0,
            "pipeline_date": "2026-07-21",
            "duration_ms": 5,
        }

    def test_pass_exits_0_and_persists(self, monkeypatch):
        persisted = self._patch_common(monkeypatch, run_result=self._result("PASS"))
        monkeypatch.setattr(sys, "argv", ["verify_ingestion.py"])
        assert verify_ingestion.main() == 0
        assert persisted["called"] is True

    def test_warn_exits_0(self, monkeypatch):
        self._patch_common(monkeypatch, run_result=self._result("WARN"))
        monkeypatch.setattr(sys, "argv", ["verify_ingestion.py"])
        assert verify_ingestion.main() == 0

    def test_fail_exits_1_but_still_persists(self, monkeypatch):
        persisted = self._patch_common(monkeypatch, run_result=self._result("FAIL"))
        monkeypatch.setattr(sys, "argv", ["verify_ingestion.py"])
        assert verify_ingestion.main() == 1
        assert persisted["called"] is True

    def test_dry_run_skips_persistence(self, monkeypatch):
        persisted = self._patch_common(monkeypatch, run_result=self._result("PASS"))
        monkeypatch.setattr(sys, "argv", ["verify_ingestion.py", "--dry-run"])
        assert verify_ingestion.main() == 0
        assert persisted["called"] is False

    def test_run_failure_exits_2(self, monkeypatch):
        self._patch_common(monkeypatch, run_raises=RuntimeError("db down"))
        monkeypatch.setattr(sys, "argv", ["verify_ingestion.py"])
        assert verify_ingestion.main() == 2

    def test_persist_failure_exits_2_even_though_checks_ran(self, monkeypatch):
        persisted = self._patch_common(
            monkeypatch, run_result=self._result("PASS"),
            persist_raises=RuntimeError("constraint violation"),
        )
        monkeypatch.setattr(sys, "argv", ["verify_ingestion.py"])
        assert verify_ingestion.main() == 2
        assert persisted["called"] is True

    def test_invalid_pipeline_date_exits_2(self, monkeypatch):
        self._patch_common(monkeypatch, run_result=self._result("PASS"))
        monkeypatch.setattr(sys, "argv", ["verify_ingestion.py", "--pipeline-date", "not-a-date"])
        assert verify_ingestion.main() == 2

    def test_invalid_batch_id_exits_2(self, monkeypatch):
        self._patch_common(monkeypatch, run_result=self._result("PASS"))
        monkeypatch.setattr(sys, "argv", ["verify_ingestion.py", "--batch-id", "not-a-guid"])
        assert verify_ingestion.main() == 2

    def test_valid_pipeline_date_and_batch_id_threaded_through(self, monkeypatch):
        seen = {}

        def _fake_run(engine, *, pipeline_date=None, batch_id=None):
            seen["pipeline_date"] = pipeline_date
            seen["batch_id"] = batch_id
            return self._result("PASS")

        monkeypatch.setattr(verify_ingestion, "load_env_file", lambda: None)
        monkeypatch.setattr(verify_ingestion, "get_engine", lambda: MagicMock())
        monkeypatch.setattr(verify_ingestion.ingest_verify, "run_all_verifications", _fake_run)
        monkeypatch.setattr(verify_ingestion.ingest_verify, "persist_verdict",
                             lambda engine, result, *, batch_id=None: uuid.uuid4())

        bid = uuid.uuid4()
        monkeypatch.setattr(sys, "argv", [
            "verify_ingestion.py", "--pipeline-date", "2026-01-05", "--batch-id", str(bid),
        ])
        assert verify_ingestion.main() == 0
        assert seen["pipeline_date"] == date(2026, 1, 5)
        assert seen["batch_id"] == bid

    def test_infra_error_message_never_includes_exception_text_on_stdout(self, monkeypatch, capsys):
        self._patch_common(monkeypatch, run_raises=RuntimeError("Server=host;Password=SuperSecret123"))
        monkeypatch.setattr(sys, "argv", ["verify_ingestion.py"])
        verify_ingestion.main()
        captured = capsys.readouterr()
        assert "SuperSecret123" not in captured.out
        assert "Password" not in captured.out


# ===========================================================================
# S2 review fix: stderr (logging) redaction of connection-string fragments
# ===========================================================================

class TestRedactSensitive:
    def test_password_and_server_redacted(self):
        out = verify_ingestion._redact_sensitive("Password=hunter2;Server=10.0.0.5")
        assert "hunter2" not in out
        assert "10.0.0.5" not in out
        assert "Password=<redacted>" in out
        assert "Server=<redacted>" in out

    def test_pwd_uid_database_redacted(self):
        out = verify_ingestion._redact_sensitive(
            "Login failed. Server=10.0.0.5;Database=AgriForecast;UID=sa;PWD=hunter2;"
        )
        assert "hunter2" not in out
        assert "sa" not in out.replace("Database=<redacted>", "")  # 'sa' only via UID
        assert "AgriForecast" not in out
        assert "10.0.0.5" not in out

    def test_case_insensitive_keys(self):
        out = verify_ingestion._redact_sensitive("password=hunter2;SERVER=10.0.0.5")
        assert "hunter2" not in out
        assert "10.0.0.5" not in out

    def test_unix_path_redacted(self):
        out = verify_ingestion._redact_sensitive(
            "FileNotFoundError: /Users/someone/Projects/Agri_Forecast/.env"
        )
        assert "/Users/someone" not in out
        assert "<path-redacted>" in out

    def test_windows_path_redacted(self):
        out = verify_ingestion._redact_sensitive(r"FileNotFoundError: C:\secrets\db.env")
        assert r"C:\secrets" not in out
        assert "<path-redacted>" in out

    def test_benign_message_passes_through_unchanged(self):
        out = verify_ingestion._redact_sensitive("Connection timed out after 30s")
        assert out == "Connection timed out after 30s"


class TestCliStderrRedaction:
    """Pins that logging.error() (stderr, via caplog) never carries an
    unredacted secret -- distinct from the pre-existing stdout-only test
    above, which only ever asserted about print() output."""

    def _patch_common(self, monkeypatch, *, run_raises=None, persist_raises=None):
        monkeypatch.setattr(verify_ingestion, "load_env_file", lambda: None)
        monkeypatch.setattr(verify_ingestion, "get_engine", lambda: MagicMock())

        def _fake_run(engine, *, pipeline_date=None, batch_id=None):
            if run_raises is not None:
                raise run_raises
            return {
                "overall": "PASS", "checks": [], "n_pass": 0, "n_warn": 0, "n_fail": 0,
                "pipeline_date": "2026-07-21", "duration_ms": 1,
            }

        monkeypatch.setattr(verify_ingestion.ingest_verify, "run_all_verifications", _fake_run)

        def _fake_persist(engine, result, *, batch_id=None):
            if persist_raises is not None:
                raise persist_raises
            return uuid.uuid4()

        monkeypatch.setattr(verify_ingestion.ingest_verify, "persist_verdict", _fake_persist)

    def test_run_failure_stderr_never_leaks_secret(self, monkeypatch, caplog):
        secret_exc = RuntimeError("Password=hunter2;Server=10.0.0.5")
        self._patch_common(monkeypatch, run_raises=secret_exc)
        monkeypatch.setattr(sys, "argv", ["verify_ingestion.py"])
        with caplog.at_level(logging.ERROR):
            exit_code = verify_ingestion.main()
        assert exit_code == 2
        assert "hunter2" not in caplog.text
        assert "10.0.0.5" not in caplog.text
        assert "RuntimeError" in caplog.text  # exception TYPE must survive redaction

    def test_persist_failure_stderr_never_leaks_secret(self, monkeypatch, caplog):
        secret_exc = RuntimeError("PWD=hunter2;Database=AgriForecast")
        self._patch_common(monkeypatch, persist_raises=secret_exc)
        monkeypatch.setattr(sys, "argv", ["verify_ingestion.py"])
        with caplog.at_level(logging.ERROR):
            exit_code = verify_ingestion.main()
        assert exit_code == 2
        assert "hunter2" not in caplog.text
        assert "AgriForecast" not in caplog.text
        assert "RuntimeError" in caplog.text

    def test_invalid_argument_stderr_never_leaks_raw_input(self, monkeypatch, caplog):
        """uuid.UUID() embeds the raw invalid input verbatim in its own
        ValueError message -- a hostile/careless --batch-id value could
        itself be (or contain) a connection-string fragment."""
        self._patch_common(monkeypatch)
        monkeypatch.setattr(sys, "argv", [
            "verify_ingestion.py", "--batch-id", "Password=hunter2;Server=10.0.0.5",
        ])
        with caplog.at_level(logging.ERROR):
            exit_code = verify_ingestion.main()
        assert exit_code == 2
        assert "hunter2" not in caplog.text
        assert "10.0.0.5" not in caplog.text
