"""Tests for the extended HARTI-Pettah fruit-history backfill (Ambul + Seeni).

Two layers:
  * PURE (always run): ext-id non-collision, idempotency partition logic.
  * DB-GATED (skip when the live DB is unreachable): PROOF that the built rows
    are row-identical to the experiment's arm-B series for the two crops, that no
    pre-splice MarketPrices rows exist (so no splice conflict), that Kolikuttu /
    Papaya are untouched, and that dry_run writes nothing.
"""
from __future__ import annotations

import pandas as pd
import pytest

from agriforecast_ml.harti import fruit_history_backfill as fhb
from agriforecast_ml.harti import loader as harti_loader

AMBUL = "FRT000003"
SEENI = "FRT000006"
KOLIKUTTU = "FRT000005"
PAPAYA = "FRT000018"


# PURE tests (no DB)
def test_ext_ids_are_the_two_adopted_crops_only():
    assert set(fhb.FRUIT_EXT_PRODUCT_IDS) == {AMBUL, SEENI}
    assert fhb.ADOPTED_CROP_CODES == (AMBUL, SEENI)


def test_ext_ids_do_not_collide_with_veg_harti_ids():
    veg_ids = set(harti_loader._HARTI_PRODUCT_IDS.values())
    fruit_ids = set(fhb.FRUIT_EXT_PRODUCT_IDS.values())
    assert fruit_ids.isdisjoint(veg_ids), "fruit ext-ids collide with veg HARTI ids"
    # Distinct per crop, negative (never the positive DEC range).
    assert len(fruit_ids) == 2
    assert all(v < 0 for v in fruit_ids)


def _fake_rows() -> pd.DataFrame:
    return pd.DataFrame([
        {"CropId": "abc", "CropCode": AMBUL, "CropName": "Banana - Abul",
         "PriceDate": pd.Timestamp("2024-01-01"), "MinPrice": 100.0,
         "MaxPrice": 120.0, "AvgPrice": 110.0, "ExternalProductId": -26,
         "ExternalProductName": "Banana - Abul"},
        {"CropId": "abc", "CropCode": AMBUL, "CropName": "Banana - Abul",
         "PriceDate": pd.Timestamp("2024-01-02"), "MinPrice": 105.0,
         "MaxPrice": 125.0, "AvgPrice": 115.0, "ExternalProductId": -26,
         "ExternalProductName": "Banana - Abul"},
    ])


def test_partition_upsert_all_insert_when_empty():
    rows = _fake_rows()
    ins, upd = fhb._partition_upsert(rows, existing_keys=set())
    assert len(ins) == 2 and len(upd) == 0


def test_partition_upsert_all_update_when_present_idempotent():
    rows = _fake_rows()
    keys = {("ABC", "2024-01-01"), ("ABC", "2024-01-02")}
    ins, upd = fhb._partition_upsert(rows, existing_keys=keys)
    assert len(ins) == 0 and len(upd) == 2, "re-run must be a no-op insert (idempotent)"


# DB-gated tests
def _engine_or_skip():
    try:
        from agriforecast_ml.envfile import load_env_file
        from agriforecast_ml.db import get_engine
        load_env_file()
        eng = get_engine()
        eng.connect().close()
        return eng
    except Exception as e:  # pragma: no cover - env-dependent
        pytest.skip(f"DB unreachable: {e}")


def _experiment_arm_b_oracle(eng, code: str) -> pd.DataFrame:
    """Independent reproduction of run_experiment.load_harti_pettah_presplice,
    restricted to one adopted crop -- the arm-B construction, byte-for-byte."""
    sql = """
        SELECT CONVERT(varchar(36), po.CropId) AS CropId, c.CropCode, c.Name AS CropName,
               po.ObservedDate AS PriceDate, po.MinPrice, po.MaxPrice
        FROM PriceObservations po
        JOIN Crops c ON c.Id = po.CropId
        JOIN Markets m ON m.Id = po.MarketId
        WHERE c.CropCode = %(code)s
          AND m.Name = 'Pettah (HARTI wholesale)'
          AND po.Source = 'HARTI'
          AND po.IsUnitConfirmed = 1
          AND po.MaxPrice > 0
          AND po.ObservedDate < '2025-05-05'
    """
    df = pd.read_sql(sql, eng, params={"code": code})
    df["PriceDate"] = pd.to_datetime(df["PriceDate"])
    for c in ("MinPrice", "MaxPrice"):
        df[c] = df[c].astype(float)
    df = (df.groupby(["CropId", "CropCode", "CropName", "PriceDate"], as_index=False)
            [["MinPrice", "MaxPrice"]].mean())
    df["AvgPrice"] = (df["MinPrice"] + df["MaxPrice"]) / 2.0
    return df.sort_values("PriceDate").reset_index(drop=True)


def test_built_rows_match_experiment_arm_b():
    """PROOF the backfilled series == experiment arm-B series for both crops."""
    eng = _engine_or_skip()
    built = fhb.build_fruit_history_rows(eng)
    assert not built.empty
    assert set(built["CropCode"].unique()) == {AMBUL, SEENI}
    for code in (AMBUL, SEENI):
        b = (built[built["CropCode"] == code]
             .sort_values("PriceDate").reset_index(drop=True))
        oracle = _experiment_arm_b_oracle(eng, code)
        assert len(b) == len(oracle), f"{code}: row count differs"
        # exact value parity on the columns load_prices() serves
        pd.testing.assert_series_equal(
            b["PriceDate"], oracle["PriceDate"], check_names=False)
        for col in ("MinPrice", "MaxPrice", "AvgPrice"):
            pd.testing.assert_series_equal(
                b[col].reset_index(drop=True), oracle[col].reset_index(drop=True),
                check_names=False, check_exact=True)
        # ext-id + source metadata correct
        assert (b["ExternalProductId"] == fhb.FRUIT_EXT_PRODUCT_IDS[code]).all()


def test_built_rows_cannot_collide_with_dec_rows():
    """Durable splice-safety invariant (holds before AND after the real backfill):
    every built HARTI row is strictly pre-splice, and no built (CropId, PriceDate)
    coincides with any existing DEC row for these crops (DEC starts at the splice)."""
    eng = _engine_or_skip()
    import sqlalchemy as sa
    built = fhb.build_fruit_history_rows(eng)
    assert (built["PriceDate"].dt.date < fhb.SPLICE_DATE).all()
    with eng.connect() as conn:
        # No DAMBULLA_DEC row for these crops exists before the splice, so the
        # built (crop, date) set can never overlap the DEC set.
        n_dec_pre = conn.execute(sa.text(
            """SELECT COUNT(*) FROM MarketPrices mp JOIN Crops c ON c.Id = mp.CropId
               WHERE c.CropCode IN (:a, :b) AND mp.Source = 'DAMBULLA_DEC'
                 AND mp.PriceDate < '2025-05-05'"""
        ), {"a": AMBUL, "b": SEENI}).scalar()
        assert n_dec_pre == 0, f"DEC rows exist pre-splice -> collision risk: {n_dec_pre}"


def test_presplice_marketprices_is_only_our_backfill():
    """Any pre-splice MarketPrices rows for these crops are HARTI-sourced (either
    none yet, pre-apply, or exactly our backfill, post-apply) -- never DEC."""
    eng = _engine_or_skip()
    import sqlalchemy as sa
    with eng.connect() as conn:
        rows = conn.execute(sa.text(
            """SELECT DISTINCT mp.Source FROM MarketPrices mp JOIN Crops c ON c.Id = mp.CropId
               WHERE c.CropCode IN (:a, :b) AND mp.PriceDate < '2025-05-05'"""
        ), {"a": AMBUL, "b": SEENI}).fetchall()
    sources = {r[0] for r in rows}
    assert sources <= {"HARTI"}, f"non-HARTI pre-splice source present: {sources}"


def test_default_build_excludes_kolikuttu_and_papaya():
    eng = _engine_or_skip()
    built = fhb.build_fruit_history_rows(eng)
    codes = set(built["CropCode"].unique())
    assert KOLIKUTTU not in codes, "Kolikuttu must NOT be adopted"
    assert PAPAYA not in codes, "Papaya must NOT be adopted"


def test_dry_run_writes_nothing():
    eng = _engine_or_skip()
    import sqlalchemy as sa

    def _count():
        with eng.connect() as conn:
            return conn.execute(sa.text(
                """SELECT COUNT(*) FROM MarketPrices mp JOIN Crops c ON c.Id = mp.CropId
                   WHERE c.CropCode IN (:a, :b) AND mp.Source = 'HARTI'"""
            ), {"a": AMBUL, "b": SEENI}).scalar()

    before = _count()
    counters = fhb.backfill_fruit_history(eng, dry_run=True)
    after = _count()
    assert before == after, "dry_run must not write"
    assert counters["built"] > 0
    # Durable (holds pre- and post-apply): every built row is accounted for as an
    # insert-or-update, and dry_run changes nothing.
    assert counters["inserted"] + counters["updated"] == counters["built"]
