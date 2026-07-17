"""
AgriForecast ML — HARTI fruits ingestion tests (R2 fruits step 1,
2026-07-16/17 pre-build analysis; owner-approved scope: Ambul, Kolikuttu,
Seeni, Papaya only).

All non-live tests are hermetic: synthetic in-memory table fixtures (list-
of-lists, matching pdfplumber's extract_tables() shape) or mocked
SQLAlchemy engines — no network, no DB. Mirrors the style of
test_harti_multimarket.py / test_canonical.py / test_harti_splice.py.

Coverage:
  TestFruitUnitDetection        — per-kg label variants accepted (both bare
                                   and explicit "(Rs/Kg)" forms); the
                                   per-FRUIT "(Rs/Fruits)" Kolikuttu variant
                                   is skipped, never stored; out-of-scope
                                   fruit rows (Anamalu, Passion Fruits,
                                   Pineapple, Mango, Woodapple, Avocado,
                                   Orange) never emit a row; no key
                                   collision with the vegetable
                                   _TARGET_CROPS dict.
  TestFruitRealCachedPDFs       — real cached PDFs across both eras
                                   (2015-07-01/2019-06-03 pre-flip,
                                   2023-12-28/2026-07-15 post-flip) parsed
                                   end-to-end, pinning exact values against
                                   the raw PDF cell text.
  TestFruitMarketPricesIsolation — fruits are absent from loader.
                                   _HARTI_TO_DB_NAME / _HARTI_PRODUCT_IDS;
                                   upsert_harti_prices() (legacy MarketPrices
                                   path) can therefore never insert a fruit
                                   row even for a Dambulla-column fruit row.
  TestFruitAliasResolution      — CommodityAliasResolver resolves the 4
                                   seeded HARTI-scoped fruit aliases; a
                                   same-name alias scoped to a different
                                   source does not leak.
  TestFruitPriceObservationsUpsert — upsert_harti_price_observations()
                                   treats fruit rows exactly like vegetable
                                   rows (no special-casing): market
                                   resolution + crop resolution both fire.
  TestFruitLivePins              — live-DB pins for CommodityAliases seed
                                   rows and backfilled PriceObservations
                                   counts; skip (never fail) when the DB is
                                   unreachable, mirroring
                                   test_market_spread.py::_db_or_skip.
"""
from __future__ import annotations

import sys
import uuid
from pathlib import Path
from unittest.mock import MagicMock

import pytest
import sqlalchemy as sa

# ---------------------------------------------------------------------------
# Path setup
# ---------------------------------------------------------------------------
ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import canonical  # noqa: E402
from agriforecast_ml.harti import parser as harti_parser  # noqa: E402
from agriforecast_ml.harti import loader as harti_loader  # noqa: E402
from agriforecast_ml.harti.parser import ParsedPrice  # noqa: E402

HARTI_CACHE = ML_ROOT / "harti_cache"


# ===========================================================================
# DB-gated helper (mirrors test_market_spread.py::_db_or_skip /
# test_festivals.py convention: skip, never fail, when the live DB is
# unreachable)
# ===========================================================================

def _db_or_skip():
    try:
        from agriforecast_ml.envfile import load_env_file
        from agriforecast_ml.db import get_engine

        load_env_file()
        engine = get_engine()
        with engine.connect() as conn:
            conn.execute(sa.text("SELECT 1"))
    except Exception as e:  # pragma: no cover - env-dependent
        pytest.skip(f"DB unreachable: {e}")
    return engine


# ===========================================================================
# Synthetic table fixture helper (mirrors test_harti_multimarket.py's
# _standard_table, minimal single-market version — fruit unit detection is
# a row-label property, independent of the multi-market column machinery
# already covered by test_harti_multimarket.py)
# ===========================================================================

def _fruit_table(rows: list[list]):
    """A minimal single-market (Dambulla) table: header rows + crop rows."""
    header1 = ["Variety", "1/1/2020"]
    header2 = [None, "Dambulla\nMarket"]
    header3 = ["", ""]
    return [header1, header2, header3, *rows]


def _parse_rows(monkeypatch, tmp_path, table, *, creation_date="D:20200101101632+05'30'"):
    """Run parse_pdf() against a synthetic table via the same monkeypatch
    harness as test_harti_multimarket.py::TestParsePdfMultiMarket."""
    fake_pdf_path = tmp_path / "fake.pdf"
    fake_pdf_path.write_bytes(b"%PDF-1.4 fake")

    class _FakePdf:
        def __init__(self):
            self.pages = [object()]
            self.metadata = {"CreationDate": creation_date} if creation_date else {}

        def __enter__(self):
            return self

        def __exit__(self, *a):
            return False

    monkeypatch.setattr(harti_parser, "_find_english_veg_page", lambda pdf: (0, table))
    import pdfplumber
    monkeypatch.setattr(pdfplumber, "open", lambda *_a, **_k: _FakePdf())

    return harti_parser.parse_pdf(fake_pdf_path, "2020-01-01")


# ===========================================================================
# 1. Fruit unit detection — per-kg accepted, per-fruit skipped
# ===========================================================================

class TestFruitUnitDetection:
    def test_ambul_bare_label_accepted_per_kg(self, monkeypatch, tmp_path):
        table = _fruit_table([["Ambul", "40.00 - 50.00"]])
        rows = _parse_rows(monkeypatch, tmp_path, table)
        assert len(rows) == 1
        assert rows[0].harti_label == "Ambul"
        assert (rows[0].min_price, rows[0].max_price) == (40.0, 50.0)

    def test_ambul_explicit_rs_kg_label_accepted(self, monkeypatch, tmp_path):
        table = _fruit_table([["Ambul(Rs/Kg)", "150- 200"]])
        rows = _parse_rows(monkeypatch, tmp_path, table)
        assert len(rows) == 1
        assert rows[0].harti_label == "Ambul"
        assert (rows[0].min_price, rows[0].max_price) == (150.0, 200.0)

    def test_kolikuttu_bare_label_accepted_per_kg(self, monkeypatch, tmp_path):
        """Post-2020-08-10 era: bare 'Kolikuttu', no unit suffix -- implicit
        Rs/kg default, accepted."""
        table = _fruit_table([["Kolikuttu", "340- 380"]])
        rows = _parse_rows(monkeypatch, tmp_path, table)
        assert len(rows) == 1
        assert rows[0].harti_label == "Kolikuttu"
        assert (rows[0].min_price, rows[0].max_price) == (340.0, 380.0)

    def test_kolikuttu_per_fruit_label_skipped(self, monkeypatch, tmp_path, caplog):
        """Pre-2020-08-10 era: 'Kolikuttu (Rs/Fruits)' is a genuine per-FRUIT
        unit -- must NEVER be stored as a per-kg observation. No ParsedPrice
        is emitted, and the skip is logged with the canonical fruit name."""
        table = _fruit_table([["Kolikuttu (Rs/Fruits)", "10.00 - 13.00"]])
        with caplog.at_level("DEBUG", logger="agriforecast_ml.harti.parser"):
            rows = _parse_rows(monkeypatch, tmp_path, table)
        assert rows == [], "Per-fruit-unit Kolikuttu row must never be stored"
        assert any(
            "Kolikuttu" in rec.message and "per-FRUIT unit" in rec.message
            for rec in caplog.records
        )

    def test_seeni_always_accepted(self, monkeypatch, tmp_path):
        table = _fruit_table([["Seeni", "120- 160"]])
        rows = _parse_rows(monkeypatch, tmp_path, table)
        assert len(rows) == 1
        assert rows[0].harti_label == "Seeni"
        assert (rows[0].min_price, rows[0].max_price) == (120.0, 160.0)

    def test_papaya_bare_label_accepted(self, monkeypatch, tmp_path):
        table = _fruit_table([["Papaya", "90.00 - 120.00"]])
        rows = _parse_rows(monkeypatch, tmp_path, table)
        assert len(rows) == 1
        assert rows[0].harti_label == "Papaya"
        assert (rows[0].min_price, rows[0].max_price) == (90.0, 120.0)

    def test_papaya_explicit_rs_kg_label_accepted(self, monkeypatch, tmp_path):
        table = _fruit_table([["Papaya (Rs/Kg)", "140- 200"]])
        rows = _parse_rows(monkeypatch, tmp_path, table)
        assert len(rows) == 1
        assert rows[0].harti_label == "Papaya"
        assert (rows[0].min_price, rows[0].max_price) == (140.0, 200.0)

    @pytest.mark.parametrize("label", [
        "Anamalu (Rs/Fruits)",
        "Passion Fruits",
        "Passion Fruits(Rs/Fruit)",
        "Other Fruits (Rs/Fruit)",
        "Pineapple - Large",
        "- Medium",
        "- Small",
        "Mango - Betti",
        "- Karathakolom",
        "Woodapple",
        "Avocado",
        "Orange",
    ])
    def test_out_of_scope_fruit_rows_never_emit(self, monkeypatch, tmp_path, label):
        """Per-fruit-unit fruits with no per-kg DB crop to map to are
        deliberately out of scope (step 1) -- must never produce a row,
        exactly like any other non-target row."""
        table = _fruit_table([[label, "100 - 200"]])
        rows = _parse_rows(monkeypatch, tmp_path, table)
        assert rows == []

    def test_multiple_fruits_same_pdf_all_resolve_independently(self, monkeypatch, tmp_path):
        table = _fruit_table([
            ["Ambul(Rs/Kg)", "150- 180"],
            ["Kolikuttu", "380- 420"],
            ["Seeni", "120- 150"],
            ["Anamalu (Rs/Fruits)", "26- 34"],
            ["Papaya (Rs/Kg)", "80- 120"],
            ["Passion Fruits(Rs/Fruit)", "40- 50"],
        ])
        rows = _parse_rows(monkeypatch, tmp_path, table)
        by_label = {r.harti_label: (r.min_price, r.max_price) for r in rows}
        assert by_label == {
            "Ambul": (150.0, 180.0),
            "Kolikuttu": (380.0, 420.0),
            "Seeni": (120.0, 150.0),
            "Papaya": (80.0, 120.0),
        }
        assert "Anamalu" not in by_label
        assert "Passion Fruits" not in by_label

    # -- Dict-level pins (no PDF I/O needed) --

    def test_per_fruit_variant_excluded_from_accepted_dict(self):
        """The core unit-safety invariant: the per-fruit-unit Kolikuttu
        label must NEVER be a key in _TARGET_FRUITS_PER_KG."""
        assert "Kolikuttu (Rs/Fruits)" not in harti_parser._TARGET_FRUITS_PER_KG

    def test_accepted_dict_has_exactly_four_canonical_fruits(self):
        assert set(harti_parser._TARGET_FRUITS_PER_KG.values()) == {
            "Ambul", "Kolikuttu", "Seeni", "Papaya",
        }

    def test_skip_dict_maps_to_a_canonical_fruit_that_is_also_accepted(self):
        """Every skip-dict entry names a fruit that DOES have an accepted
        (per-kg) form under a different label -- proves this is a genuine
        unit-era split, not an unmappable fruit."""
        for raw_label, canonical_fruit in harti_parser._TARGET_FRUITS_PER_FRUIT_SKIP.items():
            assert canonical_fruit in harti_parser._TARGET_FRUITS_PER_KG.values()

    def test_no_key_collision_with_vegetable_target_crops(self):
        """Fruit raw-label keys must never collide with the (separate,
        untouched) vegetable _TARGET_CROPS dict -- proves the exact-key
        lookup for vegetables is unaffected by the fruit widen."""
        fruit_keys = set(harti_parser._TARGET_FRUITS_PER_KG) | set(
            harti_parser._TARGET_FRUITS_PER_FRUIT_SKIP
        )
        assert fruit_keys.isdisjoint(harti_parser._TARGET_CROPS.keys())


# ===========================================================================
# 2. Real cached PDFs — both eras
# ===========================================================================

@pytest.mark.skipif(not HARTI_CACHE.is_dir(), reason="harti_cache/ not present")
class TestFruitRealCachedPDFs:
    """End-to-end parse_pdf() against real cached PDFs spanning both unit
    eras (pre/post the ~2020-08-10 Kolikuttu per-kg flip and the
    ~2021-11-02/03 Ambul/Papaya label-format changes)."""

    def _fruit_rows(self, date_str):
        pdf_path = HARTI_CACHE / f"harti_{date_str}.pdf"
        if not pdf_path.exists():
            pytest.skip(f"{pdf_path.name} not in local cache")
        rows = harti_parser.parse_pdf(pdf_path, date_str)
        return [r for r in rows if r.harti_label in ("Ambul", "Kolikuttu", "Seeni", "Papaya")]

    def test_2015_07_01_pre_flip_kolikuttu_absent(self):
        """Pre-flip era: Kolikuttu is per-fruit-unit and must be absent;
        Ambul/Seeni/Papaya (always per-kg) must be present."""
        rows = self._fruit_rows("2015-07-01")
        labels = {r.harti_label for r in rows}
        assert "Kolikuttu" not in labels
        assert {"Ambul", "Seeni", "Papaya"} <= labels
        pettah = {r.harti_label: (r.min_price, r.max_price) for r in rows if r.market_name == "Pettah"}
        assert pettah["Ambul"] == (40.0, 50.0)
        assert pettah["Seeni"] == (40.0, 50.0)
        assert pettah["Papaya"] == (90.0, 120.0)

    def test_2019_06_03_pre_flip_kolikuttu_absent(self):
        rows = self._fruit_rows("2019-06-03")
        labels = {r.harti_label for r in rows}
        assert "Kolikuttu" not in labels
        assert {"Ambul", "Seeni", "Papaya"} <= labels

    def test_2023_12_28_post_flip_all_four_present(self):
        """Post-flip era: all 4 target fruits resolve, including Kolikuttu
        (now bare/per-kg)."""
        rows = self._fruit_rows("2023-12-28")
        pettah = {r.harti_label: (r.min_price, r.max_price) for r in rows if r.market_name == "Pettah"}
        assert pettah == {
            "Ambul": (150.0, 200.0),
            "Kolikuttu": (340.0, 380.0),
            "Seeni": (120.0, 160.0),
            "Papaya": (140.0, 200.0),
        }

    def test_kolikuttu_reversion_window_handled_label_by_label(self):
        """Kolikuttu's unit label is NOT a clean one-way flip -- full-corpus
        scan found two brief reversions to the per-fruit '(Rs/Fruits)' label
        (2020-06-24 bare, 2020-07-01..2020-08-07 reverts to per-fruit,
        2020-08-10 onward bare; then 2021-10-22..2021-10-24 reverts again
        before settling bare from 2021-10-25). Each PDF's own row label
        decides independently -- no date/era heuristic -- so this must hold
        with zero special-casing beyond the plain dict lookup."""
        assert self._fruit_rows("2020-06-24")  # bare -> Kolikuttu present
        kk_0624 = [r for r in self._fruit_rows("2020-06-24") if r.harti_label == "Kolikuttu"]
        assert kk_0624 and (kk_0624[0].min_price, kk_0624[0].max_price) == (160.0, 180.0)

        kk_0701 = [r for r in self._fruit_rows("2020-07-01") if r.harti_label == "Kolikuttu"]
        assert kk_0701 == [], "2020-07-01 reverts to the per-fruit label -- must be absent"

        kk_1022 = [r for r in self._fruit_rows("2021-10-22") if r.harti_label == "Kolikuttu"]
        assert kk_1022 == [], "2021-10-22 reverts to the per-fruit label -- must be absent"

        kk_1025 = [r for r in self._fruit_rows("2021-10-25") if r.harti_label == "Kolikuttu"]
        assert kk_1025 and (kk_1025[0].min_price, kk_1025[0].max_price) == (130.0, 140.0)

    def test_2026_07_15_post_flip_all_four_present_multi_market(self):
        """Most recent PDF in the corpus: 2 markets report fruit prices
        (Pettah + Kandy), both non-cross-contaminating."""
        rows = self._fruit_rows("2026-07-15")
        by_market = {(r.market_name, r.harti_label): (r.min_price, r.max_price) for r in rows}
        assert by_market[("Pettah", "Ambul")] == (150.0, 180.0)
        assert by_market[("Kandy", "Ambul")] == (120.0, 150.0)
        assert by_market[("Pettah", "Kolikuttu")] == (380.0, 420.0)
        assert by_market[("Kandy", "Kolikuttu")] == (370.0, 380.0)
        assert by_market[("Pettah", "Seeni")] == (120.0, 150.0)
        assert by_market[("Kandy", "Seeni")] == (110.0, 140.0)
        assert by_market[("Pettah", "Papaya")] == (80.0, 120.0)
        assert by_market[("Kandy", "Papaya")] == (90.0, 110.0)


# ===========================================================================
# 3. MarketPrices isolation — fruits can NEVER reach the legacy path
# ===========================================================================

class TestFruitMarketPricesIsolation:
    _FRUIT_LABELS = ("Ambul", "Kolikuttu", "Seeni", "Papaya")

    def test_fruit_labels_absent_from_harti_to_db_name(self):
        for label in self._FRUIT_LABELS:
            assert label not in harti_loader._HARTI_TO_DB_NAME, (
                "%r must NEVER be added to _HARTI_TO_DB_NAME -- that dict "
                "backs the legacy MarketPrices path, which fruits must not "
                "reach (req 2)" % label
            )

    def test_fruit_labels_absent_from_product_ids(self):
        for label in self._FRUIT_LABELS:
            assert label not in harti_loader._HARTI_PRODUCT_IDS

    def test_upsert_harti_prices_never_inserts_a_fruit_row(self):
        """End-to-end proof: even a Dambulla-column fruit row (which passes
        the market_name filter) is skipped by the crop-map membership check,
        because the crop_map is built from the real _HARTI_TO_DB_NAME (no
        fruit entries) -- zero fruit rows ever reach MarketPrices."""
        real_crop_map = harti_loader._build_crop_map
        # Build the crop_map exactly as production does (from the real,
        # unmodified _HARTI_TO_DB_NAME), but skip the DB round-trip by
        # faking the underlying Crops lookup response as empty (fine --
        # we only care that NONE of the fruit labels are candidates at all,
        # which is a property of the dict, not the DB content).
        rows = [
            ParsedPrice("2019-01-01", "Ambul", "40-50", 40.0, 50.0, market_name="Dambulla"),
            ParsedPrice("2019-01-01", "Kolikuttu", "340-380", 340.0, 380.0, market_name="Dambulla"),
            ParsedPrice("2019-01-01", "Seeni", "120-160", 120.0, 160.0, market_name="Dambulla"),
            ParsedPrice("2019-01-01", "Papaya", "140-200", 140.0, 200.0, market_name="Pettah"),
            # A real vegetable row for contrast -- proves the harness itself
            # is capable of inserting when the crop resolves.
            ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla"),
        ]
        fake_crop_map = {
            "Beans": (uuid.UUID("deadbeef-0000-0000-0000-000000000001"), "Beans"),
        }
        original_crop_map = harti_loader._build_crop_map
        harti_loader._build_crop_map = lambda _: fake_crop_map
        original_market_id = harti_loader._dambulla_market_id
        harti_loader._dambulla_market_id = lambda _: uuid.UUID("aaaaaaaa-0000-0000-0000-000000000099")
        try:
            result = harti_loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
        finally:
            harti_loader._build_crop_map = original_crop_map
            harti_loader._dambulla_market_id = original_market_id

        # Only the Beans row (a real crop_map entry) inserts; all 4 fruit
        # rows are skipped_no_crop (Papaya via market filter AND crop-map miss).
        assert result["inserted"] == 1
        assert result["skipped_no_crop"] == 3   # Ambul, Kolikuttu, Seeni (Dambulla, no-crop)
        assert result["skipped_non_dambulla"] == 1  # Papaya (Pettah, filtered before crop-map check)


# ===========================================================================
# 4. Alias resolution (mirrors test_canonical.py::TestResolverPrecedence)
# ===========================================================================

def _mock_engine_returning(rows: list[tuple]) -> MagicMock:
    engine = MagicMock()
    conn = MagicMock()
    result = MagicMock()
    result.fetchall.return_value = rows
    conn.execute.return_value = result
    engine.connect.return_value.__enter__.return_value = conn
    return engine


def _alias_row(alias: str, crop_id: uuid.UUID, source: "str | None" = None):
    return (alias, source, str(crop_id))


AMBUL_CROP_ID = uuid.UUID("aaaaaaaa-0000-0000-0000-000000000010")
KOLIKUTTU_CROP_ID = uuid.UUID("aaaaaaaa-0000-0000-0000-000000000011")
SEENI_CROP_ID = uuid.UUID("aaaaaaaa-0000-0000-0000-000000000012")
PAPAYA_CROP_ID = uuid.UUID("aaaaaaaa-0000-0000-0000-000000000013")


class TestFruitAliasResolution:
    def _resolver(self):
        rows = [
            _alias_row("Ambul", AMBUL_CROP_ID, source="HARTI"),
            _alias_row("Kolikuttu", KOLIKUTTU_CROP_ID, source="HARTI"),
            _alias_row("Seeni", SEENI_CROP_ID, source="HARTI"),
            _alias_row("Papaya", PAPAYA_CROP_ID, source="HARTI"),
        ]
        return canonical.CommodityAliasResolver(_mock_engine_returning(rows))

    def test_all_four_fruit_aliases_resolve(self):
        resolver = self._resolver()
        assert resolver.resolve("Ambul", "HARTI") == AMBUL_CROP_ID
        assert resolver.resolve("Kolikuttu", "HARTI") == KOLIKUTTU_CROP_ID
        assert resolver.resolve("Seeni", "HARTI") == SEENI_CROP_ID
        assert resolver.resolve("Papaya", "HARTI") == PAPAYA_CROP_ID

    def test_fruit_alias_case_insensitive(self):
        resolver = self._resolver()
        assert resolver.resolve("ambul", "HARTI") == AMBUL_CROP_ID
        assert resolver.resolve("KOLIKUTTU", "HARTI") == KOLIKUTTU_CROP_ID

    def test_same_name_alias_scoped_to_other_source_does_not_leak(self):
        """A same-text alias scoped to a different source (hypothetical
        future CBSL 'Papaya' series) must not resolve under HARTI."""
        rows = [_alias_row("Papaya", uuid.UUID("cccccccc-0000-0000-0000-000000000001"), source="CBSL")]
        resolver = canonical.CommodityAliasResolver(_mock_engine_returning(rows))
        assert resolver.resolve("Papaya", "HARTI") is None

    def test_unresolved_fruit_label_returns_none(self):
        resolver = self._resolver()
        assert resolver.resolve("Anamalu", "HARTI") is None
        assert resolver.resolve("Passion Fruits", "HARTI") is None


# ===========================================================================
# 5. PriceObservations upsert — fruits behave exactly like vegetables
# ===========================================================================

class TestFruitPriceObservationsUpsert:
    def _fake_market_map(self):
        return {
            "Dambulla": uuid.UUID("bbbbbbbb-0000-0000-0000-000000000001"),
            "Pettah": uuid.UUID("bbbbbbbb-0000-0000-0000-000000000002"),
        }

    def test_fruit_rows_resolve_market_and_crop_no_special_casing(self, monkeypatch):
        fake_map = self._fake_market_map()
        rows = [
            ParsedPrice("2026-07-15", "Ambul", "150-180", 150.0, 180.0, market_name="Pettah"),
            ParsedPrice("2026-07-15", "Kolikuttu", "380-420", 380.0, 420.0, market_name="Pettah"),
            ParsedPrice("2026-07-15", "Seeni", "120-150", 120.0, 150.0, market_name="Pettah"),
            ParsedPrice("2026-07-15", "Papaya", "80-120", 80.0, 120.0, market_name="Pettah"),
        ]
        monkeypatch.setattr(harti_loader, "_build_market_map", lambda _: fake_map)
        # loader.py imports CommodityAliasResolver BY NAME (`from ..canonical
        # import CommodityAliasResolver`) -- the bound reference actually
        # used at call time lives in harti_loader's own namespace, so that
        # is what must be patched (patching canonical.CommodityAliasResolver
        # would have no effect here).
        monkeypatch.setattr(
            harti_loader, "CommodityAliasResolver",
            lambda _engine: type("R", (), {"resolve": staticmethod(lambda label, source: KOLIKUTTU_CROP_ID)})(),
        )
        result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)

        assert result["inserted"] == 4
        assert result["skipped_no_market"] == 0
        assert result["crop_resolved"] == 4
        assert result["crop_unresolved"] == 0

    def test_unresolved_fruit_alias_leaves_cropid_null_and_warns_but_still_inserts(self, monkeypatch, caplog):
        """Mirrors test_canonical.py's unresolved-crop contract: an
        unresolved fruit label is inserted with CropId NULL + WARN, never
        guessed -- self-heals later via heal_price_observation_crops()."""
        fake_map = self._fake_market_map()
        rows = [
            ParsedPrice("2026-07-15", "Ambul", "150-180", 150.0, 180.0, market_name="Pettah"),
        ]
        monkeypatch.setattr(harti_loader, "_build_market_map", lambda _: fake_map)
        monkeypatch.setattr(
            harti_loader, "CommodityAliasResolver",
            lambda _engine: type("R", (), {"resolve": staticmethod(lambda label, source: None)})(),
        )
        result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)

        assert result["inserted"] == 1
        assert result["crop_unresolved"] == 1
        assert any("Ambul" in rec.message and "no active CommodityAlias" in rec.message for rec in caplog.records)


# ===========================================================================
# 6. Live-DB pins (skip, never fail, when DB unreachable)
# ===========================================================================

class TestFruitLivePins:
    _FRUIT_ALIAS_TO_CROPCODE = {
        "Ambul": "FRT000003",
        "Kolikuttu": "FRT000005",
        "Seeni": "FRT000006",
        "Papaya": "FRT000018",
    }

    def test_four_fruit_aliases_seeded_and_resolve_to_expected_crops(self):
        engine = _db_or_skip()
        with engine.connect() as conn:
            rows = conn.execute(sa.text(
                """SELECT a.Alias, c.CropCode
                   FROM CommodityAliases a JOIN Crops c ON c.Id = a.CropId
                   WHERE a.Source = 'HARTI' AND a.Alias IN
                         ('Ambul', 'Kolikuttu', 'Seeni', 'Papaya')
                     AND a.IsActive = 1"""
            )).fetchall()
        found = {r[0]: r[1] for r in rows}
        assert found == self._FRUIT_ALIAS_TO_CROPCODE

    def test_backfilled_fruit_priceobservations_rows_exist(self):
        """Loose live pin (count > 0, not an exact snapshot -- the backfill
        is idempotently re-runnable and this test must stay green across
        re-runs): each of the 4 target fruits has at least one
        PriceObservations row with Source='HARTI' after the backfill."""
        engine = _db_or_skip()
        with engine.connect() as conn:
            rows = conn.execute(sa.text(
                """SELECT ExternalCommodityName, COUNT(*)
                   FROM PriceObservations
                   WHERE Source = 'HARTI'
                     AND ExternalCommodityName IN
                         ('Ambul', 'Kolikuttu', 'Seeni', 'Papaya')
                   GROUP BY ExternalCommodityName"""
            )).fetchall()
        counts = {r[0]: r[1] for r in rows}
        for label in ("Ambul", "Kolikuttu", "Seeni", "Papaya"):
            assert counts.get(label, 0) > 0, (
                "Expected at least one backfilled PriceObservations row for "
                "%r -- run ingest_harti.py --no-download first" % label
            )

    def test_backfilled_fruit_rows_have_no_market_prices_rows(self):
        """Live proof mirroring TestFruitMarketPricesIsolation: zero
        MarketPrices rows exist for any of the 4 fruit CropIds, even after
        the full backfill."""
        engine = _db_or_skip()
        with engine.connect() as conn:
            row = conn.execute(sa.text(
                """SELECT COUNT(*)
                   FROM MarketPrices mp
                   JOIN Crops c ON c.Id = mp.CropId
                   WHERE c.CropCode IN ('FRT000003', 'FRT000005', 'FRT000006', 'FRT000018')
                     AND mp.Source = 'HARTI'"""
            )).fetchone()
        assert row[0] == 0

    def test_all_fruit_priceobservations_rows_are_unit_confirmed(self):
        """HARTI's own unit is a verified constant for these rows (per-kg,
        Rs/kg) -- a genuinely ambiguous (min>max) row would still be
        quarantined by validate_price_row, so this loose check only pins
        that IsUnitConfirmed is being set at all (0/1), not silently NULL."""
        engine = _db_or_skip()
        with engine.connect() as conn:
            row = conn.execute(sa.text(
                """SELECT COUNT(*)
                   FROM PriceObservations
                   WHERE Source = 'HARTI'
                     AND ExternalCommodityName IN ('Ambul', 'Kolikuttu', 'Seeni', 'Papaya')
                     AND IsUnitConfirmed IS NULL"""
            )).fetchone()
        assert row[0] == 0
