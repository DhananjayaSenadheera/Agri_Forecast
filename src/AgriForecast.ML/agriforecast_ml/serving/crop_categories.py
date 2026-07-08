"""Crop -> category family for the cold-start fallback ladder (DB-backed).

WHY THIS EXISTS (P4 cold-start hardening, ClickUp 86cahefhn):
  The fallback ladder degrades per-crop -> category -> global. The category rung
  borrows a similar-crop price distribution when a crop has too little own
  history (n_obs < threshold) or is unknown but shares a category with crops that
  DO have history — a smarter prior than "all crops pooled", short of the noisier
  global prior.

SOURCE OF TRUTH — DB `CropCategories` table (R2 Step 1/4 cut-over, 2026-07-07):
  The category comes from the DB `CropCategories` table (Crops.CropCategoryId FK,
  read via `load.load_crop_categories()`, the standard load.py loader pattern).
  The retired 11-GUID / 4-family static Python map (gourd / fruit_veg / legume /
  root) is DEAD and must NOT be resurrected — a static twin of a DB calendar is a
  dual source of truth. The file's original docstring pre-authorized this: "if/when
  a DB-backed taxonomy lands, this becomes ... the DB is the source of truth."

FAMILY = the DB `category_code` (VEG, FRT, VEG-LOW, VEG-UP), used verbatim as the
  ladder "family" key. WHY NOT the old agronomic families: the DB taxonomy is
  ORTHOGONAL to the old static families and cannot reproduce them (grep-verified
  2026-07-07 across the 11 model crops):
    - old `gourd`     split across VEG-LOW (Bitter/Ridge/Cooking) AND FRT (Watermelon)
    - old `fruit_veg` split across VEG-LOW (Brinjal/Capsicum/Green Chili/Lady's F.)
                      AND bare VEG (Tomato)
    - old `legume` (Winged Bean) and `root` (Ginger) both collapse into bare VEG
  So NO honest 1:1 DB->old-family map exists. The DB's 4 codes ARE the ladder's
  4-family granularity now; the family IS the category_code (no second mapping
  layer to drift). This CHANGES the family of every model crop — see
  category_families_report() and the merge report; it is an intended taxonomy
  change, not a bug.

TRAIN/SERVE COHERENCE: `category_for` is the SINGLE producer of family strings,
  consumed symmetrically by train.model._crop_fallback (builds `by_category`
  keyed on category_for's output) and serving.predict._resolve_fallback (looks up
  `by_category` by category_for's output). Switching this function switches BOTH
  sides at the NEXT retrain, so keys stay coherent by construction. The CURRENTLY
  PROMOTED v11 payload was built with the OLD family keys; until the Step-7 retrain
  its `by_category` (old keys) will not match the new DB codes, so the category
  rung goes dead and thin crops degrade one rung further to `global`/Low — an
  honest, fail-safe degrade (never an over-confident upgrade), retired at retrain.

FAIL-CLOSED: a crop with NO category row in the DB (or an empty/unreachable
  CropCategories table) -> `None` (no-category), so the ladder skips the category
  rung straight to the global tier. We never guess a family.
"""
from __future__ import annotations

import logging

_log = logging.getLogger(__name__)

# Lazily-built, process-lifetime cache: lowercased crop-GUID -> DB category_code.
# Serving/training are read-only w.r.t. taxonomy within a run; the DB table changes
# only via migrations/seeding (a new process picks it up). None = not built yet.
_CROP_ID_CATEGORY: dict[str, str] | None = None


def _build_cache() -> dict[str, str]:
    """Build crop-GUID -> category_code from the DB. Fail-closed to {} on any
    error so serving degrades to the global tier rather than crashing."""
    try:
        from ..load import load_crop_categories

        df = load_crop_categories()
    except Exception:  # pragma: no cover - defensive; loader already degrades
        _log.exception("load_crop_categories() failed; crop taxonomy unavailable "
                       "-> fallback ladder skips the category rung (global tier).")
        return {}
    out: dict[str, str] = {}
    for _, r in df.iterrows():
        cid = r.get("crop_id")
        code = r.get("category_code")
        if cid and code:
            out[str(cid).lower()] = str(code)
    if not out:
        _log.warning("CropCategories taxonomy is empty; fallback ladder will skip "
                     "the category rung (crops degrade per-crop -> global).")
    return out


def _cache() -> dict[str, str]:
    global _CROP_ID_CATEGORY
    if _CROP_ID_CATEGORY is None:
        _CROP_ID_CATEGORY = _build_cache()
    return _CROP_ID_CATEGORY


def reset_cache() -> None:
    """Drop the memoized taxonomy (tests / after a taxonomy migration)."""
    global _CROP_ID_CATEGORY
    _CROP_ID_CATEGORY = None


def category_for(crop_id: str | None, crop_name: str | None = None) -> str | None:
    """Return the crop's DB category_code (the ladder family), or None if the crop
    has no category row (fail-closed -> ladder skips to the global tier).

    Keyed on the crop GUID (stable across renames). `crop_name` is accepted for
    signature compatibility with the retired static map's name fallback but is no
    longer used — the DB FK is authoritative and name-only lookups risked matching
    a renamed/duplicate crop to the wrong taxonomy.
    """
    if not crop_id:
        return None
    return _cache().get(str(crop_id).lower())


def category_families_report() -> dict[str, str]:
    """Diagnostic: {crop_guid -> category_code} as currently resolved from the DB.
    Used to report family CHANGES vs the retired static map at merge/review time."""
    return dict(_cache())
