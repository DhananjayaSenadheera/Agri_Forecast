"""Crop -> category family for the cold-start fallback ladder, read from the DB.

The fallback ladder degrades per-crop -> category -> global. The category rung
borrows a similar-crop price distribution when a crop has too little own history.

The DB CropCategories table is the source of truth, read via
load.load_crop_categories(). The retired static 11-GUID Python map is dead and must
NOT be resurrected - a static twin of a DB table is a second source of truth.

The family key IS the DB category_code (VEG, FRT, VEG-LOW, VEG-UP). It is orthogonal
to the old agronomic families, so there is no honest 1:1 map; the change of family
for every model crop is intended, not a bug.

category_for() is the only producer of family strings and is consumed by both
train.model._crop_fallback and serving.predict._resolve_fallback, so both sides
switch together at the next retrain. A payload built with older keys simply misses
the category rung and degrades one rung further - fail-safe, never overconfident.

A crop with no category row, or an unreachable table, gives None and the ladder
skips to the global tier. We never guess a family.
"""
from __future__ import annotations

import logging

_log = logging.getLogger(__name__)

# Lazily built, process-lifetime cache of lowercased crop GUID -> category_code.
# The taxonomy only changes through migrations, so a new process picks that up.
_CROP_ID_CATEGORY: dict[str, str] | None = None


def _build_cache() -> dict[str, str]:
    """Build crop-GUID -> category_code from the DB; fail-closed to {} so serving degrades."""
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
    """Return the crop's DB category_code (the ladder family), or None if it has none.

    Keyed on the crop GUID, which is stable across renames. crop_name is accepted for
    signature compatibility with the retired static map and is no longer used.
    """
    if not crop_id:
        return None
    return _cache().get(str(crop_id).lower())


def category_families_report() -> dict[str, str]:
    """Diagnostic: {crop_guid -> category_code} as currently resolved from the DB."""
    return dict(_cache())
