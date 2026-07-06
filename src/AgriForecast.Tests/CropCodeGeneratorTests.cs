using AgriForecast.Application.common;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using FluentAssertions;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// R2 D-DF4 (subtask 4.1) — the category-prefixed CropCode generator. New crop registrations get
/// the next VEG######/FRT###### code for their TOP-LEVEL category prefix (Up/Low-country Vegetable
/// sub-categories roll up to VEG via CropCategory.PrefixForCategory). Each prefix has its own
/// DefaultSetting counter (Veg_*/Frt_*), advanced independently. Both registration paths — the
/// manual CQRS handler and the ingestion auto-provision path — resolve the prefix from the crop's
/// CropCategoryId and call GetCropCode(prefix).
/// </summary>
public class CropCodeGeneratorTests
{
    private static (CodeSettings settings, Mock<IDefaultSettingRepository> repo, DefaultSetting seed) Build(
        int vegCode = 71, int frtCode = 27)
    {
        var seed = new DefaultSetting
        {
            Id = 1,
            Veg_Prefix = "VEG", Veg_Padding = 6, Veg_Code = vegCode,
            Frt_Prefix = "FRT", Frt_Padding = 6, Frt_Code = frtCode,
            Mkt_Prefix = "MKT", Mkt_Padding = 8, Mkt_Code = 7,
        };
        var repo = new Mock<IDefaultSettingRepository>();
        repo.Setup(r => r.GetDefaultSetting()).ReturnsAsync(seed);
        return (new CodeSettings(repo.Object), repo, seed);
    }

    // ── Prefix rollup (CropCategory.PrefixForCategory) ───────────────────────────────────────

    [Fact]
    public void Vegetable_TopLevel_ResolvesToVegPrefix()
        => CropCategory.PrefixForCategory(CropCategory.VegetableId).Should().Be("VEG");

    [Fact]
    public void Fruit_TopLevel_ResolvesToFrtPrefix()
        => CropCategory.PrefixForCategory(CropCategory.FruitId).Should().Be("FRT");

    [Fact]
    public void UpCountryVegetable_SubCategory_RollsUpToVeg()
        => CropCategory.PrefixForCategory(CropCategory.UpCountryVegetableId).Should().Be("VEG");

    [Fact]
    public void LowCountryVegetable_SubCategory_RollsUpToVeg()
        => CropCategory.PrefixForCategory(CropCategory.LowCountryVegetableId).Should().Be("VEG");

    [Fact]
    public void UnknownCategory_DefaultsToVeg()
        => CropCategory.PrefixForCategory(Guid.NewGuid()).Should().Be("VEG");

    // ── Code stamping per prefix ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCropCode_Veg_StampsPaddedCodeFromVegCounter()
    {
        var (settings, _, _) = Build(vegCode: 71);
        (await settings.GetCropCode("VEG")).Should().Be("VEG000071");
    }

    [Fact]
    public async Task GetCropCode_Frt_StampsPaddedCodeFromFrtCounter()
    {
        var (settings, _, _) = Build(frtCode: 27);
        (await settings.GetCropCode("FRT")).Should().Be("FRT000027");
    }

    [Fact]
    public async Task GetCropCode_UnknownPrefix_FallsBackToVegCounter()
    {
        var (settings, _, _) = Build(vegCode: 71);
        (await settings.GetCropCode("XYZ")).Should().Be("VEG000071");
    }

    // ── Counters advance independently ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetCropCode_Veg_AdvancesOnlyVegCounter()
    {
        var (settings, repo, seed) = Build(vegCode: 71, frtCode: 27);
        await settings.GetCropCode("VEG");
        seed.Veg_Code.Should().Be(72);
        seed.Frt_Code.Should().Be(27, "the fruit counter must not move when a vegetable is coded");
        repo.Verify(r => r.UpdateDefaultSetting(seed), Times.Once);
    }

    [Fact]
    public async Task GetCropCode_Frt_AdvancesOnlyFrtCounter()
    {
        var (settings, _, seed) = Build(vegCode: 71, frtCode: 27);
        await settings.GetCropCode("FRT");
        seed.Frt_Code.Should().Be(28);
        seed.Veg_Code.Should().Be(71, "the vegetable counter must not move when a fruit is coded");
    }

    [Fact]
    public async Task GetCropCode_Sequential_IssuesConsecutiveCodesPerPrefix()
    {
        var (settings, _, _) = Build(vegCode: 71, frtCode: 27);
        (await settings.GetCropCode("VEG")).Should().Be("VEG000071");
        (await settings.GetCropCode("VEG")).Should().Be("VEG000072");
        (await settings.GetCropCode("FRT")).Should().Be("FRT000027");
        (await settings.GetCropCode("VEG")).Should().Be("VEG000073");
    }

    // ── End-to-end: a sub-category crop registers under the VEG prefix ───────────────────────

    [Fact]
    public async Task SubCategoryCrop_RegistersUnderVegPrefix()
    {
        var (settings, _, _) = Build(vegCode: 71);
        var prefix = CropCategory.PrefixForCategory(CropCategory.LowCountryVegetableId);
        (await settings.GetCropCode(prefix)).Should().Be("VEG000071");
    }
}
