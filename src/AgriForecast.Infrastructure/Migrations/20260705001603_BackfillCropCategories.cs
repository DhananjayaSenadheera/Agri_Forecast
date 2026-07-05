using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillCropCategories : Migration
    {
        // R2 Step 1.2 — name-keyed backfill of Crops.CropCategoryId against the
        // 96 live Crop rows. Crop GUIDs are per-database (auto-provisioned), so
        // this is deliberately a data UPDATE keyed on Name, NOT HasData/seed.
        // Category GUIDs are the fixed CropCategories seed GUIDs from
        // 20260705001104_AddCropCategory:
        //   VEG (top-level)  d4c40001-0000-0000-0000-000000000001
        //   FRT              d4c40001-0000-0000-0000-000000000002
        //   VEG-UP           d4c40001-0000-0000-0000-000000000003
        //   VEG-LOW          d4c40001-0000-0000-0000-000000000004
        // Ambiguous crops (Tomato, onions, pulses, spices, grains, yams,
        // aquatic vegetables, etc.) go to top-level VEG — never a guessed
        // subcategory. 'Passion' has two Crop rows; the name-keyed UPDATE
        // covers both.
        private const string Veg = "d4c40001-0000-0000-0000-000000000001";
        private const string Frt = "d4c40001-0000-0000-0000-000000000002";
        private const string VegUp = "d4c40001-0000-0000-0000-000000000003";
        private const string VegLow = "d4c40001-0000-0000-0000-000000000004";

        // FRT (25 distinct names)
        private const string FruitNames =
            "N'Ambarella', N'Avacado', N'Banana - Abul', N'Banana - Ambun', " +
            "N'Banana - Kolikuttu', N'Banana - Sini', N'Coconut', N'Gooseberry', " +
            "N'Guava', N'Indian bael', N'Jackfruit', N'King Coconut', N'Lime', " +
            "N'Mango - Karatha Kolomban', N'Mango - Malu', N'Mango- Other', " +
            "N'Mango- TJC', N'Papaya', N'Passion', N'Pineapple', N'Red Banana', " +
            "N'Soursop', N'Tamarind', N'Watermelon', N'Woodapple'";

        // VEG-UP (17 distinct names) — cool-climate up-country vegetables
        private const string UpCountryNames =
            "N'Beans', N'Beetroot - Nuwaraeliya', N'Beetroot Cut - Malsiripura', " +
            "N'Beetroot Cut - Puththalama', N'Cabbage', N'Carrot - Jaffna', " +
            "N'Cauliflower', N'Leeks', N'Lettuce', N'Nnolkhol', " +
            "N'Nuwaraeliya Carrot', N'Potatoes - Import', N'Potatoes - Jaffna', " +
            "N'Potatoes - Nuwaraeliya', N'Potatoes - Walimada', N'Raddish', " +
            "N'Red Cabbage'";

        // VEG-LOW (18 distinct names) — low-country vegetables
        private const string LowCountryNames =
            "N'Bitter Gourd', N'Brinjal', N'Butternut Squash', N'Capsicum', " +
            "N'Cooking Melon', N'Cucumber', N'Drumsticks', N'Eggplant', " +
            "N'Green Chili', N'Lady''s Fingers', N'Nai Miris', N'Pumpkin - Big', " +
            "N'Pumpkin - Malashian', N'Ridge Gourd', N'Snake Gourd', " +
            "N'Thai Eggplant', N'Thumbakarawila', N'Yard - Long Beans'";

        // VEG top-level (35 distinct names) — ambiguous / not a definitive
        // up-country or low-country vegetable (onions, pulses, grains, spices,
        // yams, aquatic greens, Tomato grown in both zones, etc.)
        private const string TopLevelVegNames =
            "N'Ash Plantain', N'Athugowa', N'Big Onion', N'Big Onion Import', " +
            "N'Big Onion Lanka', N'Black Pepper', N'Chaw - Chaw', N'Corns', " +
            "N'Cowpea', N'Curry Leaves', N'Dry Chillies', N'Finger Millet', " +
            "N'Ginger', N'Gotukola', N'Gram', N'Green Gram', N'Kiriala', " +
            "N'Kohila', N'Lotus Roots', N'Manioc', N'Mukunuwenna', " +
            "N'Onion Leaves', N'Peanuts', N'Plantain Flower', N'Red Onion Import', " +
            "N'Red Onion- Lanka', N'Sesame', N'Soya Bean', N'Sweet Potato', " +
            "N'Thithbatu', N'Tomato', N'Turmeric', N'Water Spinach', " +
            "N'Wild -Baby Potato', N'Winged Bean'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"UPDATE Crops SET CropCategoryId = '{Frt}' WHERE Name IN ({FruitNames});");
            migrationBuilder.Sql(
                $"UPDATE Crops SET CropCategoryId = '{VegUp}' WHERE Name IN ({UpCountryNames});");
            migrationBuilder.Sql(
                $"UPDATE Crops SET CropCategoryId = '{VegLow}' WHERE Name IN ({LowCountryNames});");
            migrationBuilder.Sql(
                $"UPDATE Crops SET CropCategoryId = '{Veg}' WHERE Name IN ({TopLevelVegNames});");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"UPDATE Crops SET CropCategoryId = NULL WHERE Name IN ({FruitNames});");
            migrationBuilder.Sql(
                $"UPDATE Crops SET CropCategoryId = NULL WHERE Name IN ({UpCountryNames});");
            migrationBuilder.Sql(
                $"UPDATE Crops SET CropCategoryId = NULL WHERE Name IN ({LowCountryNames});");
            migrationBuilder.Sql(
                $"UPDATE Crops SET CropCategoryId = NULL WHERE Name IN ({TopLevelVegNames});");
        }
    }
}
