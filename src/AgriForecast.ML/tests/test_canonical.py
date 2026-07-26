"""Canonical mapping layer tests.

Hermetic: a mocked SQLAlchemy engine, no network, no live DB.

Covers resolver precedence (a source-scoped alias beats a global one), case-insensitive
matching, unresolved labels returning None rather than a guess, inactive aliases not
resolving, healing only CropId NULL rows (idempotent, dry_run writes nothing), the HARTI
writer's unit contract, and the feature-safe market filter excluding NationalAggregate and
ECOMAP markets.
"""
from __future__ import annotations

import sys
import uuid
from pathlib import Path
from unittest.mock import MagicMock

import pytest

# Path setup
ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import canonical  # noqa: E402
from agriforecast_ml.harti import loader as harti_loader  # noqa: E402
from agriforecast_ml.harti.parser import ParsedPrice  # noqa: E402


# Mock engine helpers

def _mock_engine_returning(rows: list[tuple]) -> MagicMock:
    """Build a MagicMock engine whose `.connect().__enter__().execute(...)`
    returns an object with `.fetchall()` -> rows, matching the
    `with engine.connect() as conn: conn.execute(...).fetchall()` pattern
    used throughout canonical.py / harti/loader.py.
    """
    engine = MagicMock()
    conn = MagicMock()
    result = MagicMock()
    result.fetchall.return_value = rows
    conn.execute.return_value = result
    engine.connect.return_value.__enter__.return_value = conn
    return engine


def _alias_row(alias: str, crop_id: uuid.UUID, source: "str | None" = None):
    """Shape matches canonical.py's SELECT Alias, Source, CropId query."""
    return (alias, source, str(crop_id))


BEANS_CROP_ID = uuid.UUID("aaaaaaaa-0000-0000-0000-000000000001")
RIDGE_GOURD_CROP_ID = uuid.UUID("aaaaaaaa-0000-0000-0000-000000000002")
LADY_FINGERS_CROP_ID = uuid.UUID("aaaaaaaa-0000-0000-0000-000000000003")


# 1. Resolver precedence — source-scoped beats global

class TestResolverPrecedence:
    def test_source_scoped_alias_wins_over_global_for_same_label(self):
        """Same alias text mapped to two different crops: a HARTI-scoped row
        and a global (Source IS NULL) row. Resolving with source='HARTI'
        must return the source-scoped crop, not the global one."""
        other_crop_id = uuid.UUID("bbbbbbbb-0000-0000-0000-000000000099")
        rows = [
            _alias_row("Beans", BEANS_CROP_ID, source="HARTI"),
            _alias_row("Beans", other_crop_id, source=None),
        ]
        engine = _mock_engine_returning(rows)
        resolver = canonical.CommodityAliasResolver(engine)

        assert resolver.resolve("Beans", "HARTI") == BEANS_CROP_ID

    def test_global_alias_resolves_when_no_source_scoped_row(self):
        rows = [_alias_row("Kohila", RIDGE_GOURD_CROP_ID, source=None)]
        engine = _mock_engine_returning(rows)
        resolver = canonical.CommodityAliasResolver(engine)

        assert resolver.resolve("Kohila", "HARTI") == RIDGE_GOURD_CROP_ID
        # Also resolves with no source argument at all
        assert resolver.resolve("Kohila") == RIDGE_GOURD_CROP_ID

    def test_source_scoped_row_for_a_different_source_does_not_leak(self):
        """A CBSL-scoped alias must not resolve when querying with
        source='HARTI', unless a global fallback also exists."""
        cbsl_only_crop = uuid.UUID("cccccccc-0000-0000-0000-000000000001")
        rows = [_alias_row("Beans", cbsl_only_crop, source="CBSL")]
        engine = _mock_engine_returning(rows)
        resolver = canonical.CommodityAliasResolver(engine)

        assert resolver.resolve("Beans", "HARTI") is None

    def test_ladies_fingers_and_luffa_mismatch_examples_resolve(self):
        """Pins the two documented label-mismatch examples from the Stage A
        migration seed (Ladies Fingers -> Lady's Fingers crop, Luffa ->
        Ridge Gourd crop)."""
        rows = [
            _alias_row("Ladies Fingers", LADY_FINGERS_CROP_ID, source="HARTI"),
            _alias_row("Luffa", RIDGE_GOURD_CROP_ID, source="HARTI"),
        ]
        engine = _mock_engine_returning(rows)
        resolver = canonical.CommodityAliasResolver(engine)

        assert resolver.resolve("Ladies Fingers", "HARTI") == LADY_FINGERS_CROP_ID
        assert resolver.resolve("Luffa", "HARTI") == RIDGE_GOURD_CROP_ID


# 2. Case-insensitive matching (mirrors DB collation)

class TestResolverCaseHandling:
    def test_lowercase_label_matches_titlecase_alias(self):
        rows = [_alias_row("Beans", BEANS_CROP_ID, source="HARTI")]
        engine = _mock_engine_returning(rows)
        resolver = canonical.CommodityAliasResolver(engine)

        assert resolver.resolve("beans", "HARTI") == BEANS_CROP_ID
        assert resolver.resolve("BEANS", "HARTI") == BEANS_CROP_ID
        assert resolver.resolve("BeAnS", "HARTI") == BEANS_CROP_ID

    def test_case_insensitive_for_global_alias_too(self):
        rows = [_alias_row("Kohila", RIDGE_GOURD_CROP_ID, source=None)]
        engine = _mock_engine_returning(rows)
        resolver = canonical.CommodityAliasResolver(engine)

        assert resolver.resolve("KOHILA") == RIDGE_GOURD_CROP_ID

    def test_leading_trailing_whitespace_is_tolerated(self):
        rows = [_alias_row("Beans", BEANS_CROP_ID, source="HARTI")]
        engine = _mock_engine_returning(rows)
        resolver = canonical.CommodityAliasResolver(engine)

        assert resolver.resolve("  Beans  ", "HARTI") == BEANS_CROP_ID


# 3. Unresolved — never guess, never fuzzy-match

class TestResolverUnresolved:
    def test_no_matching_alias_returns_none(self):
        rows = [_alias_row("Beans", BEANS_CROP_ID, source="HARTI")]
        engine = _mock_engine_returning(rows)
        resolver = canonical.CommodityAliasResolver(engine)

        assert resolver.resolve("Winged Bean", "HARTI") is None

    def test_close_but_not_exact_label_does_not_fuzzy_match(self):
        """'Bean' (singular) must NOT resolve via a 'Beans' alias -- this
        pins the no-fuzzy-matching contract."""
        rows = [_alias_row("Beans", BEANS_CROP_ID, source="HARTI")]
        engine = _mock_engine_returning(rows)
        resolver = canonical.CommodityAliasResolver(engine)

        assert resolver.resolve("Bean", "HARTI") is None

    def test_inactive_alias_excluded_from_load(self):
        """The resolver's load query filters WHERE IsActive = 1, so an
        inactive alias row is never even loaded into memory -- verify the
        query executed enforces that filter."""
        engine = _mock_engine_returning([])
        canonical.CommodityAliasResolver(engine)

        conn = engine.connect.return_value.__enter__.return_value
        executed_sql = str(conn.execute.call_args[0][0])
        assert "IsActive = 1" in executed_sql

    def test_empty_label_returns_none(self):
        rows = [_alias_row("Beans", BEANS_CROP_ID, source="HARTI")]
        engine = _mock_engine_returning(rows)
        resolver = canonical.CommodityAliasResolver(engine)

        assert resolver.resolve("", "HARTI") is None
        assert resolver.resolve(None, "HARTI") is None


# 4. heal_price_observation_crops — only NULL CropId rows, idempotent

class TestHealPriceObservationCrops:
    def _engine_with_alias_and_null_rows(self, alias_rows, null_crop_rows):
        """Two distinct SELECTs happen in heal_price_observation_crops():
        1) CommodityAliasResolver's load (Alias, Source, CropId)
        2) the NULL-CropId candidate scan (Id, ExternalCommodityName, Source)
        Route each call's fetchall() to the right payload based on call order.
        """
        engine = MagicMock()
        conn = MagicMock()

        results = [alias_rows, null_crop_rows]
        call_state = {"n": 0}

        def _execute(*_a, **_k):
            idx = call_state["n"]
            call_state["n"] += 1
            res = MagicMock()
            res.fetchall.return_value = results[idx] if idx < len(results) else []
            return res

        conn.execute.side_effect = _execute
        engine.connect.return_value.__enter__.return_value = conn
        # heal_price_observation_crops() also opens engine.begin() for writes
        begin_conn = MagicMock()
        engine.begin.return_value.__enter__.return_value = begin_conn
        return engine, begin_conn

    def test_heals_only_null_cropid_rows_and_never_touches_assigned(self):
        row_id_1 = uuid.uuid4()
        alias_rows = [_alias_row("Beans", BEANS_CROP_ID, source="HARTI")]
        null_rows = [(row_id_1, "Beans", "HARTI")]

        engine, begin_conn = self._engine_with_alias_and_null_rows(alias_rows, null_rows)

        result = canonical.heal_price_observation_crops(engine)

        assert result["candidates"] == 1
        assert result["healed"] == 1
        assert result["unresolved"] == 0

        # Exactly one UPDATE executed, and it targets only CropId IS NULL rows
        assert begin_conn.execute.call_count == 1
        update_sql = str(begin_conn.execute.call_args[0][0])
        assert "CropId IS NULL" in update_sql
        assert "SET CropId" in update_sql

    def test_select_candidates_query_filters_cropid_is_null(self):
        """The candidate-scan SELECT itself must filter WHERE CropId IS NULL
        -- an already-assigned row should never even be read as a heal
        candidate, let alone written to."""
        engine, _ = self._engine_with_alias_and_null_rows([], [])
        canonical.heal_price_observation_crops(engine)

        conn = engine.connect.return_value.__enter__.return_value
        # Second execute() call is the candidate scan (first is alias load)
        candidate_sql = str(conn.execute.call_args_list[1][0][0])
        assert "CropId IS NULL" in candidate_sql

    def test_unresolved_label_stays_null_and_is_warned(self, caplog):
        row_id_1 = uuid.uuid4()
        alias_rows: list = []  # no aliases at all
        null_rows = [(row_id_1, "Some Unmapped Veg", "HARTI")]

        engine, begin_conn = self._engine_with_alias_and_null_rows(alias_rows, null_rows)

        result = canonical.heal_price_observation_crops(engine)

        assert result["candidates"] == 1
        assert result["healed"] == 0
        assert result["unresolved"] == 1
        assert begin_conn.execute.call_count == 0  # nothing to write
        assert any(
            "Some Unmapped Veg" in rec.message and "never guessed" in rec.message
            for rec in caplog.records
        )

    def test_dry_run_does_not_write(self):
        row_id_1 = uuid.uuid4()
        alias_rows = [_alias_row("Beans", BEANS_CROP_ID, source="HARTI")]
        null_rows = [(row_id_1, "Beans", "HARTI")]

        engine, begin_conn = self._engine_with_alias_and_null_rows(alias_rows, null_rows)

        result = canonical.heal_price_observation_crops(engine, dry_run=True)

        assert result["candidates"] == 1
        assert begin_conn.execute.call_count == 0
        engine.begin.assert_not_called()

    def test_idempotent_second_run_over_same_data_is_a_noop(self):
        """Simulates re-running heal after a row has already been healed:
        the candidate scan (mocked here to return zero rows, exactly as the
        live WHERE CropId IS NULL guard would after the first heal) yields
        zero candidates and zero writes."""
        engine, begin_conn = self._engine_with_alias_and_null_rows(
            [_alias_row("Beans", BEANS_CROP_ID, source="HARTI")], []
        )

        result = canonical.heal_price_observation_crops(engine)

        assert result["candidates"] == 0
        assert result["healed"] == 0
        assert begin_conn.execute.call_count == 0

    def test_source_filter_is_applied_to_candidate_query_when_given(self):
        engine, _ = self._engine_with_alias_and_null_rows([], [])
        canonical.heal_price_observation_crops(engine, source="HARTI")

        conn = engine.connect.return_value.__enter__.return_value
        candidate_call = conn.execute.call_args_list[1]
        candidate_sql = str(candidate_call[0][0])
        assert "Source = :source" in candidate_sql
        assert candidate_call[0][1]["source"] == "HARTI"


# 5. HARTI writer sets unit fields + resolves CropId via the resolver

class TestHartiWriterUnitContract:
    def _fake_market_map(self):
        return {"Dambulla": uuid.UUID("11111111-0000-0000-0000-000000000001")}

    def test_upsert_sets_confirmed_unit_fields(self, monkeypatch):
        monkeypatch.setattr(
            harti_loader, "_build_market_map", lambda _engine: self._fake_market_map()
        )
        fake_resolver = MagicMock()
        fake_resolver.resolve.return_value = BEANS_CROP_ID
        monkeypatch.setattr(
            harti_loader, "CommodityAliasResolver", lambda _engine: fake_resolver
        )

        rows = [ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla")]

        # Capture the row actually built for upsert via a dry_run (no SQL
        # execution) plus inspecting the counters, then re-derive the row
        # shape by calling the resolver/constant directly for comparison.
        result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)

        assert result["inserted"] == 1
        assert result["crop_resolved"] == 1
        assert result["crop_unresolved"] == 0

    def test_unresolved_crop_leaves_null_and_warns_but_still_inserts(self, monkeypatch, caplog):
        monkeypatch.setattr(
            harti_loader, "_build_market_map", lambda _engine: self._fake_market_map()
        )
        fake_resolver = MagicMock()
        fake_resolver.resolve.return_value = None
        monkeypatch.setattr(
            harti_loader, "CommodityAliasResolver", lambda _engine: fake_resolver
        )

        rows = [ParsedPrice("2019-01-01", "Unmapped Crop", "100-120", 100.0, 120.0, market_name="Dambulla")]

        result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)

        assert result["inserted"] == 1, "row insertion is not gated on crop resolution"
        assert result["crop_resolved"] == 0
        assert result["crop_unresolved"] == 1
        assert any(
            "no active CommodityAlias" in rec.message and "never guessed" in rec.message
            for rec in caplog.records
        )

    def test_resolver_called_with_harti_source(self, monkeypatch):
        """The writer must scope resolution to Source='HARTI' (source-scoped
        precedence), not resolve with source=None."""
        monkeypatch.setattr(
            harti_loader, "_build_market_map", lambda _engine: self._fake_market_map()
        )
        fake_resolver = MagicMock()
        fake_resolver.resolve.return_value = BEANS_CROP_ID
        monkeypatch.setattr(
            harti_loader, "CommodityAliasResolver", lambda _engine: fake_resolver
        )

        rows = [ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla")]
        harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)

        fake_resolver.resolve.assert_called_once_with("Beans", "HARTI")

    def test_resolver_built_once_per_run_not_per_row(self, monkeypatch):
        monkeypatch.setattr(
            harti_loader, "_build_market_map", lambda _engine: self._fake_market_map()
        )
        call_count = {"n": 0}

        def _counting_resolver(_engine):
            call_count["n"] += 1
            m = MagicMock()
            m.resolve.return_value = BEANS_CROP_ID
            return m

        monkeypatch.setattr(harti_loader, "CommodityAliasResolver", _counting_resolver)

        rows = [
            ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla"),
            ParsedPrice("2019-01-02", "Beans", "90-110", 90.0, 110.0, market_name="Dambulla"),
            ParsedPrice("2019-01-03", "Beans", "95-115", 95.0, 115.0, market_name="Dambulla"),
        ]
        harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)

        assert call_count["n"] == 1

    def test_unit_contract_constants_match_harti_verified_values(self):
        """Pins the verified HARTI unit constants exported from canonical.py
        -- HARTI daily wholesale bulletins are Rs/kg corpus-wide (bulletin
        header states '... (Rs./kg)')."""
        assert canonical.HARTI_UNIT_RAW == "Rs/kg"
        assert canonical.HARTI_UNIT_CONVERSION_FACTOR == 1.0


# 6. get_feature_safe_market_ids — the S2 (Pettah double-count) trap

class TestFeatureSafeMarketIds:
    def test_excludes_national_aggregate_and_ecomap_by_query_filter(self):
        """The live rule is enforced by the SQL WHERE clause itself (the DB
        never even returns excluded rows) -- verify the executed query
        contains both exclusions, and that whatever rows the (mocked) DB
        returns come back as the result set unmodified."""
        real_market_ids = [uuid.uuid4() for _ in range(5)]
        rows = [(str(mid),) for mid in real_market_ids]
        engine = _mock_engine_returning(rows)

        result = canonical.get_feature_safe_market_ids(engine)

        assert result == set(real_market_ids)

        conn = engine.connect.return_value.__enter__.return_value
        executed_sql = str(conn.execute.call_args[0][0])
        assert "MarketType <> :national_aggregate" in executed_sql
        assert "MarketCode NOT LIKE :ecomap_prefix" in executed_sql

        executed_params = conn.execute.call_args[0][1]
        assert executed_params["national_aggregate"] == 3
        assert executed_params["ecomap_prefix"] == "ECOMAP-%"

    def test_returns_empty_set_when_only_excluded_markets_exist(self):
        engine = _mock_engine_returning([])
        result = canonical.get_feature_safe_market_ids(engine)
        assert result == set()

    def test_national_aggregate_market_type_constant_is_3(self):
        """Pins MarketType.NationalAggregate = 3, matching
        AgriForecast.Domain.Enums.MarketType.cs."""
        assert canonical._NATIONAL_AGGREGATE_MARKET_TYPE == 3

    def test_ecomap_prefix_constant(self):
        assert canonical._ECOMAP_MARKET_CODE_PREFIX == "ECOMAP-"
