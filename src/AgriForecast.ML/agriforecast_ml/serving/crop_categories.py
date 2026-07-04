"""Provisional crop -> category taxonomy for the cold-start fallback ladder.

WHY THIS EXISTS (P4 cold-start hardening, ClickUp 86cahefhn):
  The fallback ladder degrades per-crop -> category -> global. There is currently
  NO category column on the .NET `Crop` entity / `Crops` table (verified 2026-07-04),
  so this Python-side static map is the simplest viable taxonomy for the 11 model
  crops. It is PROVISIONAL and intentionally kept off the .NET migration path this
  phase — if/when a DB-backed taxonomy lands, this becomes a fallback for unmapped
  crops and the DB is the source of truth.

  Keys are Crop GUIDs (lowercased) to be robust to name spelling / renames; a
  name-based secondary map is provided for readability and for crops that reach
  serving by name only.

The category tier is a MIDDLE rung: when a crop has too little own history to
trust its per-crop quantiles (n_obs < threshold) OR is unknown but shares a
category with crops that DO have history, we borrow the category-level price
distribution instead of jumping straight to the (much noisier) global prior.
"""
from __future__ import annotations

# Crop GUID (lowercased) -> category. Covers the 11 model crops (2026-07-04 DB).
# Grouping is agronomic/culinary and deliberately coarse — the point is a smarter
# prior than "all crops pooled", not a botanical classification.
_CROP_ID_CATEGORY: dict[str, str] = {
    # Gourds / cucurbits
    "482d04a5-8148-409e-bc01-7b8af4e83a9a": "gourd",        # Bitter Gourd
    "e384f58a-addf-40a4-aff0-10bebe8bc08f": "gourd",        # Ridge Gourd
    "127e5593-7d4f-4fa0-8f07-69e567071567": "gourd",        # Cooking Melon
    "939d91c0-d423-420e-9194-ffa625533276": "gourd",        # Watermelon
    # Fruiting vegetables
    "b44bacdb-042f-4286-ac23-02bc4cac4486": "fruit_veg",    # Brinjal
    "c0dd18f8-e4b9-483f-8b93-d07363c8803f": "fruit_veg",    # Capsicum
    "e1ba5835-6554-43dd-b55d-d189cdae74d6": "fruit_veg",    # Green Chili
    "61bf3543-0739-4981-850e-affbe57a5dae": "fruit_veg",    # Tomato
    "74870612-2ce3-41e2-a74e-73f4f1f3aec2": "fruit_veg",    # Lady's Fingers
    # Legumes
    "79b2c22a-cd8a-4b6e-9cec-c2abaaaa04e2": "legume",       # Winged Bean
    # Roots / rhizomes
    "b725df40-9475-4254-84bd-5d0c68339a60": "root",         # Ginger
}

# Name (lowercased) -> category, secondary lookup for name-only callers / new crops
# that share a spelling with a model crop. Kept in sync with the GUID map above.
_CROP_NAME_CATEGORY: dict[str, str] = {
    "bitter gourd": "gourd",
    "ridge gourd": "gourd",
    "cooking melon": "gourd",
    "watermelon": "gourd",
    "snake gourd": "gourd",       # covered crop with no v10 ML path, still categorisable
    "brinjal": "fruit_veg",
    "capsicum": "fruit_veg",
    "green chili": "fruit_veg",
    "tomato": "fruit_veg",
    "lady's fingers": "fruit_veg",
    "ladies fingers": "fruit_veg",
    "beans": "legume",
    "winged bean": "legume",
    "ginger": "root",
}


def category_for(crop_id: str | None, crop_name: str | None = None) -> str | None:
    """Return the category for a crop, or None if unmapped.

    Prefers the GUID map (stable across renames); falls back to the name map.
    """
    if crop_id:
        cat = _CROP_ID_CATEGORY.get(str(crop_id).lower())
        if cat:
            return cat
    if crop_name:
        return _CROP_NAME_CATEGORY.get(str(crop_name).strip().lower())
    return None
