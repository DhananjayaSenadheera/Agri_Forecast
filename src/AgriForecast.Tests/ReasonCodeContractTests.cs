using System.Text.Json;
using AgriForecast.Application.Requests.Forecast.Quaries.GetHarvest;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// API-5: the additive ML fields (reasonCode / reasonParams / topFactors) must
/// deserialize off the ML wire when PRESENT (current service) and when ABSENT
/// (older service - additive non-breaking both directions), and pass through to
/// the FE-facing DTO preserving the omitted-vs-empty distinction for topFactors.
/// </summary>
public class ReasonCodeContractTests
{
    // Mirrors HarvestPredictionClient.JsonOptions exactly.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ──────────────────────────────────────────────────────────────────────────
    // 1. Contract deserialization - model-served shape (all new fields present)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Predict_ModelServed_DeserializesReasonCodeParamsAndTopFactors()
    {
        const string json = """
        {
          "cropId": "abc",
          "predictedPrice": 120.5,
          "lowerBound": 100.0,
          "upperBound": 140.0,
          "confidence": "High",
          "activePredictor": "model",
          "explanation": "x",
          "reasonCode": "model_served",
          "reasonParams": {},
          "topFactors": [
            { "code": "rainfall_30d", "direction": "up",      "weight": 0.62 },
            { "code": "usd_lkr",      "direction": "down",    "weight": 0.25 },
            { "code": "policy_flag",  "direction": "neutral", "weight": 0.13 }
          ]
        }
        """;

        var dto = JsonSerializer.Deserialize<HarvestPredictionDto>(json, JsonOptions)!;

        dto.ReasonCode.Should().Be("model_served");
        dto.ReasonParams.Should().NotBeNull().And.BeEmpty();
        dto.TopFactors.Should().NotBeNull();
        dto.TopFactors!.Should().HaveCount(3);
        dto.TopFactors![0].Code.Should().Be("rainfall_30d");
        dto.TopFactors![0].Direction.Should().Be("up");
        dto.TopFactors![0].Weight.Should().Be(0.62m);
        dto.TopFactors![2].Direction.Should().Be("neutral");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. Contract deserialization - fallback shape (topFactors OMITTED, params carry ints)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Predict_Fallback_TopFactorsOmitted_StaysNull_AndIntParamsPreserved()
    {
        const string json = """
        {
          "cropId": "abc",
          "predictedPrice": 90.0,
          "lowerBound": 60.0,
          "upperBound": 120.0,
          "confidence": "Low",
          "activePredictor": "crop_mean_fallback",
          "explanation": "x",
          "reasonCode": "not_model_served",
          "reasonParams": { "neededHistoryObs": 24, "historyObs": 3 }
        }
        """;

        var dto = JsonSerializer.Deserialize<HarvestPredictionDto>(json, JsonOptions)!;

        dto.ReasonCode.Should().Be("not_model_served");
        // topFactors absent on the wire => null (NOT an empty list) so the FE can
        // distinguish "no breakdown" from "empty breakdown".
        dto.TopFactors.Should().BeNull();

        dto.ReasonParams.Should().ContainKeys("neededHistoryObs", "historyObs");
        dto.ReasonParams!["neededHistoryObs"].GetInt32().Should().Be(24);
        dto.ReasonParams!["historyObs"].GetInt32().Should().Be(3);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 3. Contract deserialization - OLD ML service (none of the fields present)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Predict_OldMlService_MissingAllNewFields_DoesNotThrow_NullsFlow()
    {
        const string json = """
        {
          "cropId": "abc",
          "predictedPrice": 100.0,
          "lowerBound": 80.0,
          "upperBound": 120.0,
          "confidence": "Medium",
          "activePredictor": "model",
          "explanation": "x"
        }
        """;

        var act = () => JsonSerializer.Deserialize<HarvestPredictionDto>(json, JsonOptions);

        var dto = act.Should().NotThrow().Which!;
        dto.ReasonCode.Should().BeNull();
        dto.ReasonParams.Should().BeNull();
        dto.TopFactors.Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4. reasonParams preserves arbitrary future keys and value types as-is
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Predict_ReasonParams_PreservesArbitraryKeysAndValueKinds()
    {
        const string json = """
        {
          "cropId": "abc",
          "predictedPrice": 100.0,
          "lowerBound": 80.0,
          "upperBound": 120.0,
          "confidence": "High",
          "activePredictor": "model",
          "explanation": "x",
          "reasonCode": "some_future_code",
          "reasonParams": { "intVal": 7, "strVal": "hello", "boolVal": true, "floatVal": 1.5 }
        }
        """;

        var dto = JsonSerializer.Deserialize<HarvestPredictionDto>(json, JsonOptions)!;

        dto.ReasonParams!["intVal"].ValueKind.Should().Be(JsonValueKind.Number);
        dto.ReasonParams!["intVal"].GetInt32().Should().Be(7);
        dto.ReasonParams!["strVal"].GetString().Should().Be("hello");
        dto.ReasonParams!["boolVal"].GetBoolean().Should().BeTrue();
        dto.ReasonParams!["floatVal"].GetDecimal().Should().Be(1.5m);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 5. Timeline contract - reasonCode/reasonParams present AND absent (no throw)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Timeline_Present_DeserializesReasonCodeAndParams_NoTopFactors()
    {
        const string json = """
        {
          "cropName": "Tomato",
          "activePredictor": "crop_mean_fallback",
          "confidence": "Low",
          "explanation": "x",
          "reasonCode": "adequate_history_fallback",
          "reasonParams": { "neededHistoryObs": 24 },
          "history": [],
          "forecast": []
        }
        """;

        var dto = JsonSerializer.Deserialize<CropTimelineDto>(json, JsonOptions)!;

        dto.ReasonCode.Should().Be("adequate_history_fallback");
        dto.ReasonParams!["neededHistoryObs"].GetInt32().Should().Be(24);
    }

    [Fact]
    public void Timeline_OldMlService_MissingReasonFields_DoesNotThrow_NullsFlow()
    {
        const string json = """
        {
          "cropName": "Tomato",
          "activePredictor": "model",
          "confidence": "High",
          "explanation": "x",
          "history": [],
          "forecast": []
        }
        """;

        var act = () => JsonSerializer.Deserialize<CropTimelineDto>(json, JsonOptions);

        var dto = act.Should().NotThrow().Which!;
        dto.ReasonCode.Should().BeNull();
        dto.ReasonParams.Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 6. Passthrough mapping into the FE-facing HarvestForecast_GetDto
    // ──────────────────────────────────────────────────────────────────────────

    private static (GetHarvestForecastQueryHandler handler, Mock<IHarvestPredictionClient> clientMock) BuildHandler()
    {
        var repMock = new Mock<IMarketPriceRepository>();
        repMock.Setup(r => r.GetRecentByCropIdAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(new List<MarketPrice>
               {
                   new()
                   {
                       Id = Guid.NewGuid(),
                       CropId = Guid.NewGuid(),
                       MinPrice = 100m,
                       MaxPrice = 100m,
                       PriceDate = DateOnly.FromDateTime(DateTime.UtcNow),
                       Source = "test"
                   }
               });
        var clientMock = new Mock<IHarvestPredictionClient>();
        var handler = new GetHarvestForecastQueryHandler(
            repMock.Object, clientMock.Object, Mock.Of<ILogger<GetHarvestForecastQueryHandler>>());
        return (handler, clientMock);
    }

    private static HarvestPredictionDto BasePrediction() => new()
    {
        CropId = Guid.NewGuid().ToString(),
        PlantDate = "2026-01-01",
        PredictedPrice = 110m,
        LowerBound = 99m,
        UpperBound = 121m,
        Confidence = "High",
        ActivePredictor = "model",
        Explanation = "x"
    };

    private static GetHarvestForecastQuery Query() => new()
    {
        CropId = Guid.NewGuid(),
        PlantDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1))
    };

    [Fact]
    public async Task Handle_ModelServed_PassesReasonCodeParamsAndTopFactorsThrough()
    {
        var (handler, clientMock) = BuildHandler();

        var prediction = BasePrediction();
        prediction.ReasonCode = "model_served";
        prediction.ReasonParams = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{}");
        prediction.TopFactors = new List<TopFactorDto>
        {
            new() { Code = "rainfall_30d", Direction = "up", Weight = 0.62m }
        };

        clientMock.Setup(c => c.PredictAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(Query(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.ReasonCode.Should().Be("model_served");
        result.Data.ReasonParams.Should().NotBeNull().And.BeEmpty();
        result.Data.TopFactors.Should().NotBeNull();
        result.Data.TopFactors!.Should().ContainSingle();
        result.Data.TopFactors![0].Code.Should().Be("rainfall_30d");
    }

    [Fact]
    public async Task Handle_Fallback_OmittedTopFactors_StayNullInFeDto()
    {
        var (handler, clientMock) = BuildHandler();

        var prediction = BasePrediction();
        prediction.ActivePredictor = "crop_mean_fallback";
        prediction.Confidence = "Low";
        prediction.ReasonCode = "not_model_served";
        prediction.ReasonParams = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{ "neededHistoryObs": 24, "historyObs": 3 }""");
        prediction.TopFactors = null; // ML omitted it

        clientMock.Setup(c => c.PredictAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(Query(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.ReasonCode.Should().Be("not_model_served");
        // omitted stays omitted - never surfaces as an empty list.
        result.Data.TopFactors.Should().BeNull();
        result.Data.ReasonParams!["neededHistoryObs"].GetInt32().Should().Be(24);
    }
}
