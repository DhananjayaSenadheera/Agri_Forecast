using System.Reflection;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// Regression tests for the hand-written static mappers that replaced AutoMapper
/// (security fix F-05: AutoMapper 12.0.1 / GHSA-rvv3-g6hj-g44x removed).
/// These assert the exact ForMember/PreCondition semantics the old AutoMapper
/// profiles encoded, which are otherwise invisible from the mapper code alone.
/// Style mirrors PolicyFlagTests / ResultAndContractTests already in this project.
/// </summary>
public class MapperTests
{
    // ──────────────────────────────────────────────────────────────────────────────
    // CropMapper — create
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Crop_ToEntity_AssignsNonEmptyUniqueId()
    {
        var dto = new Crop_CreateDto { Name = "Carrot", ExternalProductId = 12, Source = "Dambulla" };

        var a = dto.ToEntity();
        var b = dto.ToEntity();

        a.Id.Should().NotBe(Guid.Empty);
        b.Id.Should().NotBe(Guid.Empty);
        a.Id.Should().NotBe(b.Id, "each create call must mint a fresh identity");
    }

    [Fact]
    public void Crop_ToEntity_SetsCreatedAndUpdatedAt_ToUtcNow()
    {
        var before = DateTime.UtcNow;
        var crop = new Crop_CreateDto { Name = "Carrot", ExternalProductId = 12, Source = "Dambulla" }.ToEntity();
        var after = DateTime.UtcNow;

        crop.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        crop.UpdatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Crop_ToEntity_CopiesNameExternalProductIdAndSource()
    {
        var dto = new Crop_CreateDto { Name = "Carrot", ExternalProductId = 12, Source = "Dambulla" };

        var crop = dto.ToEntity();

        crop.Name.Should().Be("Carrot");
        crop.ExternalProductId.Should().Be(12);
        crop.Source.Should().Be("Dambulla");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // CropMapper — update (conditional-copy guards, mutate-in-place, immutability)
    // ──────────────────────────────────────────────────────────────────────────────

    private static Crop ExistingCrop() => Crop.CreateForManualEntry("Original", 1, "OriginalSource", Guid.NewGuid());

    [Fact]
    public void Crop_ApplyTo_OverwritesNameEvenWhenSourceNameIsNull()
    {
        // Legacy AutoMapper ForMember(Name, MapFrom(src.Name)) had no PreCondition —
        // it copied unconditionally, including null. This must be preserved exactly.
        var existing = ExistingCrop();
        var dto = new Crop_UpdateDto { Id = existing.Id, Name = null, ExternalProductId = null, Source = null };

        var result = dto.ApplyTo(existing);

        result.Name.Should().BeNull();
    }

    [Fact]
    public void Crop_ApplyTo_OverwritesNameWhenProvided()
    {
        var existing = ExistingCrop();
        var dto = new Crop_UpdateDto { Id = existing.Id, Name = "Renamed", ExternalProductId = null, Source = null };

        dto.ApplyTo(existing);

        existing.Name.Should().Be("Renamed");
    }

    [Fact]
    public void Crop_ApplyTo_ExternalProductId_CopiedOnlyWhenHasValue()
    {
        var existing = ExistingCrop();
        existing.ExternalProductId.Should().Be(1);
        var dto = new Crop_UpdateDto { Id = existing.Id, Name = "x", ExternalProductId = null, Source = null };

        dto.ApplyTo(existing);

        existing.ExternalProductId.Should().Be(1, "PreCondition(HasValue) must skip the copy when the update omits it");
    }

    [Fact]
    public void Crop_ApplyTo_ExternalProductId_OverwrittenWhenProvided()
    {
        var existing = ExistingCrop();
        var dto = new Crop_UpdateDto { Id = existing.Id, Name = "x", ExternalProductId = 99, Source = null };

        dto.ApplyTo(existing);

        existing.ExternalProductId.Should().Be(99);
    }

    [Fact]
    public void Crop_ApplyTo_Source_CopiedOnlyWhenNotNull()
    {
        var existing = ExistingCrop();
        existing.Source.Should().Be("OriginalSource");
        var dto = new Crop_UpdateDto { Id = existing.Id, Name = "x", ExternalProductId = null, Source = null };

        dto.ApplyTo(existing);

        existing.Source.Should().Be("OriginalSource", "PreCondition(!= null) must skip the copy when the update omits it");
    }

    [Fact]
    public void Crop_ApplyTo_Source_OverwrittenWhenProvided()
    {
        var existing = ExistingCrop();
        var dto = new Crop_UpdateDto { Id = existing.Id, Name = "x", ExternalProductId = null, Source = "NewSource" };

        dto.ApplyTo(existing);

        existing.Source.Should().Be("NewSource");
    }

    [Fact]
    public void Crop_ApplyTo_RefreshesUpdatedAt()
    {
        var existing = ExistingCrop();
        existing.UpdatedAt = DateTime.UtcNow.AddDays(-5);
        var dto = new Crop_UpdateDto { Id = existing.Id, Name = "x" };

        var before = DateTime.UtcNow;
        dto.ApplyTo(existing);
        var after = DateTime.UtcNow;

        existing.UpdatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Crop_ApplyTo_DoesNotChangeIdOrCreatedAt()
    {
        var existing = ExistingCrop();
        var originalId = existing.Id;
        var originalCreatedAt = existing.CreatedAt;
        var dto = new Crop_UpdateDto { Id = existing.Id, Name = "x", ExternalProductId = 5, Source = "s" };

        dto.ApplyTo(existing);

        existing.Id.Should().Be(originalId);
        existing.CreatedAt.Should().Be(originalCreatedAt);
    }

    [Fact]
    public void Crop_ApplyTo_MutatesTheSameInstance_ReferenceEquality()
    {
        // EF change tracking depends on the tracked instance being mutated in place,
        // not replaced with a new object.
        var existing = ExistingCrop();
        var dto = new Crop_UpdateDto { Id = existing.Id, Name = "x" };

        var result = dto.ApplyTo(existing);

        ReferenceEquals(result, existing).Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // CropMapper — Crop -> Crop_GetDto
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Crop_ToGetDto_CopiesAllExposedFieldsIncludingId()
    {
        var crop = Crop.CreateForManualEntry("Carrot", 7, "Dambulla", Guid.NewGuid());
        crop.CropCode = "CR-001";

        var dto = crop.ToGetDto();

        dto.Id.Should().Be(crop.Id);
        dto.Name.Should().Be(crop.Name);
        dto.ExternalProductId.Should().Be(crop.ExternalProductId);
        dto.Source.Should().Be(crop.Source);
        dto.CreatedAt.Should().Be(crop.CreatedAt);
        dto.UpdatedAt.Should().Be(crop.UpdatedAt);
    }

    [Fact]
    public void Crop_ToGetDto_NoPropertyLeftAtDefault_WhenSourceFullyPopulated()
    {
        // Reflection-based round-trip completeness guard: every public settable
        // property on Crop_GetDto must differ from a fresh default(Crop_GetDto)
        // when the source Crop has non-default values for the same-named field.
        var crop = Crop.CreateForManualEntry("Carrot", 7, "Dambulla", Guid.NewGuid());

        var dto = crop.ToGetDto();
        var blank = new Crop_GetDto();

        foreach (var prop in typeof(Crop_GetDto).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            var dtoValue = prop.GetValue(dto);
            var blankValue = prop.GetValue(blank);
            Assert.NotEqual(blankValue, dtoValue);
        }
    }

    [Fact]
    public void Crop_ToGetDtoList_PreservesOrderAndCount()
    {
        var crops = new[]
        {
            Crop.CreateForManualEntry("A", 1, "s1", Guid.NewGuid()),
            Crop.CreateForManualEntry("B", 2, "s2", Guid.NewGuid()),
            Crop.CreateForManualEntry("C", 3, "s3", Guid.NewGuid())
        };

        var dtos = crops.ToGetDtoList();

        dtos.Should().HaveCount(3);
        dtos.Select(d => d.Name).Should().ContainInOrder("A", "B", "C");
        dtos.Select(d => d.Id).Should().ContainInOrder(crops.Select(c => c.Id));
    }

    // R2 D-DF3: the EconomicCenterMapper tests were removed with the EconomicCenters CRUD
    // retirement (mapper + Eco_* DTOs deleted). Market registration is covered by
    // MarketMapperTests / MarketDomainTests / MarketCreateValidatorTests.

    // ──────────────────────────────────────────────────────────────────────────────
    // PolicyFlagMapper — create (date-only normalisation, nullable EffectiveTo)
    // ──────────────────────────────────────────────────────────────────────────────

    private static PolicyFlag_CreateDto ValidPolicyFlagDto(DateTime from, DateTime? to) => new()
    {
        PolicyType = PolicyType.ImportBan,
        Title = "Test policy",
        Description = "desc",
        EffectiveFrom = from,
        EffectiveTo = to,
        Direction = PolicyDirection.Bullish,
        Source = "Gov",
        ReferenceUrl = "http://example.com"
    };

    [Fact]
    public void PolicyFlag_ToEntity_AssignsNonEmptyUniqueId()
    {
        var dto = ValidPolicyFlagDto(new DateTime(2022, 1, 1), null);

        var a = dto.ToEntity();
        var b = dto.ToEntity();

        a.Id.Should().NotBe(Guid.Empty);
        a.Id.Should().NotBe(b.Id);
    }

    [Fact]
    public void PolicyFlag_ToEntity_NormalisesEffectiveFromToDateOnly()
    {
        // Leakage guard: any time-of-day component must be stripped.
        var withTime = new DateTime(2022, 5, 17, 14, 37, 22);
        var dto = ValidPolicyFlagDto(withTime, null);

        var flag = dto.ToEntity();

        flag.EffectiveFrom.Should().Be(new DateTime(2022, 5, 17));
        flag.EffectiveFrom.TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void PolicyFlag_ToEntity_NormalisesEffectiveToToDateOnly_WhenProvided()
    {
        var withTime = new DateTime(2022, 6, 30, 23, 59, 59);
        var dto = ValidPolicyFlagDto(new DateTime(2022, 1, 1), withTime);

        var flag = dto.ToEntity();

        flag.EffectiveTo.Should().Be(new DateTime(2022, 6, 30));
        flag.EffectiveTo!.Value.TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void PolicyFlag_ToEntity_EffectiveTo_StaysNull_WhenSourceIsNull()
    {
        // Still-active / open-ended policy: null must be preserved, not coerced to a date.
        var dto = ValidPolicyFlagDto(new DateTime(2022, 1, 1), null);

        var flag = dto.ToEntity();

        flag.EffectiveTo.Should().BeNull();
    }

    [Fact]
    public void PolicyFlag_ToEntity_SetsCreatedAtUtc_ToUtcNow()
    {
        var before = DateTime.UtcNow;
        var flag = ValidPolicyFlagDto(new DateTime(2022, 1, 1), null).ToEntity();
        var after = DateTime.UtcNow;

        flag.CreatedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void PolicyFlag_ToEntity_CopiesRemainingFields()
    {
        var dto = ValidPolicyFlagDto(new DateTime(2022, 1, 1), new DateTime(2022, 6, 1));

        var flag = dto.ToEntity();

        flag.PolicyType.Should().Be(dto.PolicyType);
        flag.Title.Should().Be(dto.Title);
        flag.Description.Should().Be(dto.Description);
        flag.Direction.Should().Be(dto.Direction);
        flag.Source.Should().Be(dto.Source);
        flag.ReferenceUrl.Should().Be(dto.ReferenceUrl);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // PolicyFlagMapper — PolicyFlag -> PolicyFlag_GetDto / ToGetDtoList
    // ──────────────────────────────────────────────────────────────────────────────

    private static PolicyFlag FullyPopulatedFlag() => new()
    {
        Id = Guid.NewGuid(),
        PolicyType = PolicyType.ImportBan,
        Title = "Rice import ban",
        Description = "desc",
        EffectiveFrom = new DateTime(2022, 1, 1),
        EffectiveTo = new DateTime(2022, 6, 1),
        Direction = PolicyDirection.Bullish,
        Source = "Gov",
        ReferenceUrl = "http://example.com",
        CreatedAtUtc = DateTime.UtcNow
    };

    [Fact]
    public void PolicyFlag_ToGetDto_CopiesEveryField()
    {
        var flag = FullyPopulatedFlag();

        var dto = flag.ToGetDto();

        dto.Id.Should().Be(flag.Id);
        dto.PolicyType.Should().Be(flag.PolicyType);
        dto.Title.Should().Be(flag.Title);
        dto.Description.Should().Be(flag.Description);
        dto.EffectiveFrom.Should().Be(flag.EffectiveFrom);
        dto.EffectiveTo.Should().Be(flag.EffectiveTo);
        dto.Direction.Should().Be(flag.Direction);
        dto.Source.Should().Be(flag.Source);
        dto.ReferenceUrl.Should().Be(flag.ReferenceUrl);
        dto.CreatedAtUtc.Should().Be(flag.CreatedAtUtc);
    }

    [Fact]
    public void PolicyFlag_ToGetDto_NoPropertyLeftAtDefault_WhenSourceFullyPopulated()
    {
        var flag = FullyPopulatedFlag();

        var dto = flag.ToGetDto();
        var blank = new PolicyFlag_GetDto();

        foreach (var prop in typeof(PolicyFlag_GetDto).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            var dtoValue = prop.GetValue(dto);
            var blankValue = prop.GetValue(blank);
            Assert.NotEqual(blankValue, dtoValue);
        }
    }

    [Fact]
    public void PolicyFlag_ToGetDto_PreservesNullEffectiveTo()
    {
        var flag = FullyPopulatedFlag();
        flag.EffectiveTo = null;

        var dto = flag.ToGetDto();

        dto.EffectiveTo.Should().BeNull();
    }

    [Fact]
    public void PolicyFlag_ToGetDtoList_PreservesOrderAndCount()
    {
        var flags = new[]
        {
            FullyPopulatedFlag(),
            FullyPopulatedFlag(),
            FullyPopulatedFlag()
        };

        var dtos = flags.ToGetDtoList();

        dtos.Should().HaveCount(3);
        dtos.Select(d => d.Id).Should().ContainInOrder(flags.Select(f => f.Id));
    }
}
