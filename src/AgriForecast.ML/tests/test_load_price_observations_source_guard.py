"""R2 Step 2 (DEC -> PriceObservations mirror, ClickUp 86cajtx2k) -- MANDATORY
identity-preservation proof for ``load.load_price_observations()``.

CONTEXT: HARTI already writes Dambulla rows into PriceObservations. The DEC
mirror (dec_mirror.py) additively writes Source='DAMBULLA_DEC' rows at the
SAME Dambulla market. ``load_price_observations()`` feeds the cross-market
spread features (_attach_market_spread in features.py) via a per-
(MarketSlug, CropId, ObservedDate) aggregation that is otherwise completely
SOURCE-AGNOSTIC (its SELECT list does not even include Source) -- so without
an explicit guard, mirroring DEC rows would silently BLEND them into the
Dambulla aggregate the moment the backfill runs, moving CropFeatureDaily's
spread features (and therefore the promoted v16 frame) with no code change
anywhere near ``features.py`` itself.

The fix is a single added ``Source = :source`` filter in
``load_price_observations()``'s SQL (default 'HARTI', i.e. today's only
writer) -- this file is the PROOF that the filter neutralises a simulated
mirror, via an in-memory union (no DB write, per the task brief).

Two layers of proof:
  1. TestFakeRelationalReadSql -- exercises load_price_observations() against
     a fake in-memory "PriceObservations" table via a monkeypatched
     pd.read_sql that actually HONOURS the SQL text's WHERE Source clause
     (not a rows-are-whatever-I-return stub) -- this is the real proof: the
     PRE table (HARTI only) and the POST table (PRE + simulated mirrored
     DAMBULLA_DEC rows unioned in, in-memory) produce byte-identical output.
  2. TestRegressionGuard -- proves the test harness itself is not vacuous: the
     SAME POST table, read WITHOUT the source filter (simulating the
     pre-R2-Step-2 query), DOES change vs PRE -- i.e. the mirror would have
     been a real regression had the guard not been added.
"""
from __future__ import annotations

import sys
import uuid
from pathlib import Path

import pandas as pd
import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

import agriforecast_ml.load as L  # noqa: E402
from agriforecast_ml import canonical  # noqa: E402


DAMBULLA_ID = uuid.UUID("b2a20001-0000-0000-0000-000000000001")
PETTAH_ID = uuid.UUID("b2a20001-0000-0000-0000-000000000004")
CROP_A = uuid.UUID("aaaaaaaa-0000-0000-0000-000000000001")

_SAFE_IDS = {DAMBULLA_ID, PETTAH_ID}


def _fake_read_sql_factory(table: pd.DataFrame):
    """Build a fake pd.read_sql that ACTUALLY evaluates the query's WHERE
    Source clause against `table` (which carries a Source column the real
    SELECT list never returns), so the test proves the SQL text's filter --
    not just that some canned frame was returned regardless of the query.

    Mirrors the rest of the real query's filters too (MarketId membership,
    IsUnitConfirmed, MaxPrice>0, CropId not null) so the fake is a faithful
    stand-in for what SQL Server would actually compute.
    """
    def _fake_read_sql(sql, engine, params=None):
        sql_text = str(sql)
        params = params or {}
        df = table.copy()

        # MarketId IN (...) -- resolve the market ids bound in params.
        market_ids = {v for k, v in params.items() if k.startswith("m")}
        df = df[df["MarketId"].isin(market_ids)]

        df = df[(df["IsUnitConfirmed"] == 1) & (df["MaxPrice"] > 0) & df["CropId"].notna()]

        # The identity guard under test: only apply the Source filter if the
        # SQL text actually asked for one (this is what makes
        # TestRegressionGuard below a genuine "guard removed" simulation).
        if "po.Source = :source" in sql_text:
            df = df[df["Source"] == params.get("source")]

        out = df.rename(columns={"MarketName": "MarketName"})[
            ["MarketName", "CropId", "ObservedDate", "MinPrice", "MaxPrice"]
        ].copy()
        return out

    return _fake_read_sql


def _market_name(market_id: uuid.UUID) -> str:
    return "Dambulla Dedicated Economic Centre" if market_id == DAMBULLA_ID \
        else "Pettah (HARTI wholesale)"


def _row(market_id, crop_id, date_str, min_p, max_p, source, is_unit_confirmed=1):
    return {
        "MarketId": str(market_id), "MarketName": _market_name(market_id),
        "CropId": str(crop_id), "ObservedDate": date_str,
        "MinPrice": min_p, "MaxPrice": max_p,
        "Source": source, "IsUnitConfirmed": is_unit_confirmed,
    }


def _run(monkeypatch, table: pd.DataFrame, *, source="HARTI") -> pd.DataFrame:
    monkeypatch.setattr(L, "get_engine", lambda: object())
    monkeypatch.setattr(canonical, "get_feature_safe_market_ids", lambda engine: _SAFE_IDS)
    monkeypatch.setattr(L.pd, "read_sql", _fake_read_sql_factory(table))
    if source is _UNSET:
        return L.load_price_observations()
    return L.load_price_observations(source=source)


_UNSET = object()


# 1. Identity proof: simulated mirror (in-memory union) does not move output.

class TestFakeRelationalReadSql:
    def test_mirrored_dec_rows_at_dambulla_do_not_change_output(self, monkeypatch):
        """PRE: HARTI-only PriceObservations at Dambulla+Pettah. POST: PRE plus
        a simulated DEC mirror row at the SAME (market, crop, date) as an
        existing HARTI row, with a DELIBERATELY DIFFERENT price (999.0) so any
        leak into the aggregation would be immediately visible. With the
        Source='HARTI' guard, PRE and POST must be BYTE-IDENTICAL."""
        pre = pd.DataFrame([
            _row(DAMBULLA_ID, CROP_A, "2025-06-01", 100.0, 120.0, "HARTI"),
            _row(PETTAH_ID, CROP_A, "2025-06-01", 90.0, 110.0, "HARTI"),
        ])
        mirrored_extra = pd.DataFrame([
            # Simulates the mirror: same market/crop/date, DEC source, an
            # extreme "trap" price that must NOT leak into the aggregate.
            _row(DAMBULLA_ID, CROP_A, "2025-06-01", 999.0, 999.0, "DAMBULLA_DEC"),
        ])
        post = pd.concat([pre, mirrored_extra], ignore_index=True)

        out_pre = _run(monkeypatch, pre)
        out_post = _run(monkeypatch, post)

        pd.testing.assert_frame_equal(
            out_pre.reset_index(drop=True), out_post.reset_index(drop=True),
            check_like=False,
        )

    def test_mirrored_dec_rows_at_a_new_date_do_not_appear(self, monkeypatch):
        """A mirrored DEC row on a date with NO prior HARTI coverage must also
        not appear in the guarded output (it should be filtered before ever
        reaching the MarketSlug/CropId/ObservedDate aggregation)."""
        pre = pd.DataFrame([
            _row(DAMBULLA_ID, CROP_A, "2025-06-01", 100.0, 120.0, "HARTI"),
        ])
        post = pd.concat([pre, pd.DataFrame([
            _row(DAMBULLA_ID, CROP_A, "2025-06-02", 55.0, 60.0, "DAMBULLA_DEC"),
        ])], ignore_index=True)

        out_post = _run(monkeypatch, post)
        assert len(out_post) == 1
        assert out_post.iloc[0]["ObservedDate"] == pd.Timestamp("2025-06-01")

    def test_default_source_parameter_is_harti(self, monkeypatch):
        """Calling with no explicit source= argument must still guard (the
        production call sites in build_features.py never pass source=) --
        pins the default value itself, not just the explicit-arg behaviour."""
        pre = pd.DataFrame([_row(DAMBULLA_ID, CROP_A, "2025-06-01", 100.0, 120.0, "HARTI")])
        post = pd.concat([pre, pd.DataFrame([
            _row(DAMBULLA_ID, CROP_A, "2025-06-01", 999.0, 999.0, "DAMBULLA_DEC"),
        ])], ignore_index=True)

        out_pre = _run(monkeypatch, pre, source=_UNSET)
        out_post = _run(monkeypatch, post, source=_UNSET)
        pd.testing.assert_frame_equal(out_pre.reset_index(drop=True), out_post.reset_index(drop=True))


# 2. Regression guard: proves the harness is not vacuously passing.

class TestRegressionGuard:
    def test_without_the_source_filter_the_mirror_WOULD_have_changed_output(self, monkeypatch):
        """Sanity check on the test harness itself: re-run the exact same
        PRE/POST tables through the fake WITHOUT the Source filter applied
        (source=None disables the WHERE clause in load_price_observations,
        proving the fake genuinely reacts to the SQL text) -- output MUST
        differ, demonstrating this is a real regression the guard prevents,
        not a test that would pass no matter what."""
        pre = pd.DataFrame([
            _row(DAMBULLA_ID, CROP_A, "2025-06-01", 100.0, 120.0, "HARTI"),
        ])
        post = pd.concat([pre, pd.DataFrame([
            _row(DAMBULLA_ID, CROP_A, "2025-06-01", 999.0, 999.0, "DAMBULLA_DEC"),
        ])], ignore_index=True)

        out_pre = _run(monkeypatch, pre, source=None)
        out_post_unguarded = _run(monkeypatch, post, source=None)

        dambulla_pre = out_pre[out_pre["MarketSlug"] == "Dambulla"]["AvgPrice"].iloc[0]
        dambulla_post = out_post_unguarded[out_post_unguarded["MarketSlug"] == "Dambulla"]["AvgPrice"].iloc[0]
        assert dambulla_pre != dambulla_post, (
            "test harness is vacuous: removing the source filter should have "
            "changed the Dambulla aggregate (HARTI 110.0 alone vs blended "
            "with the DEC 999.0 trap value) but did not"
        )
        # Concretely: (100+120)/2=110 alone vs blended with (999+999)/2=999 -> mean 554.5.
        assert dambulla_pre == pytest.approx(110.0)
        assert dambulla_post == pytest.approx(554.5)
