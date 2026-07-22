using AgriForecast.Application.Requests.Forecast.Quaries.GetCropReadiness;
using AgriForecast.Application.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

// GET /api/forecast/crop-readiness handler (crop-status colouring, UI 2026-07-22).
// The handler is a passthrough-with-flattening: the ML map's serving decision is
// consumed verbatim (never re-derived), the GUID-string-keyed dict becomes a typed
// list, and only a transport failure (null client return) is a Failure — the
// honest empty map is a valid success.
public class CropReadinessHandlerTests
{
    private static readonly string CropA = Guid.NewGuid().ToString();
    private static readonly string CropB = Guid.NewGuid().ToString();

    private static (GetCropReadinessQueryHandler handler, Mock<IHarvestPredictionClient> clientMock) BuildHandler()
    {
        var clientMock = new Mock<IHarvestPredictionClient>();
        var handler = new GetCropReadinessQueryHandler(
            clientMock.Object, Mock.Of<ILogger<GetCropReadinessQueryHandler>>());
        return (handler, clientMock);
    }

    [Fact]
    public async Task Maps_ml_dict_to_typed_list_verbatim()
    {
        var (handler, clientMock) = BuildHandler();
        clientMock.Setup(c => c.GetCropReadinessAsync(default)).ReturnsAsync(new CropReadinessDto
        {
            ModelVersion = "v17",
            MinHistoryObs = 365,
            ModelActive = true,
            Crops = new Dictionary<string, CropReadinessEntryDto>
            {
                [CropA] = new() { Ready = true, NObs = 900 },
                [CropB] = new() { Ready = false, NObs = 120 },
            },
        });

        var result = await handler.Handle(new GetCropReadinessQuery(), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("v17", result.Data!.ModelVersion);
        Assert.Equal(365, result.Data.MinHistoryObs);
        Assert.True(result.Data.ModelActive);
        Assert.Equal(2, result.Data.Crops.Count);
        var a = result.Data.Crops.Single(c => c.CropId == Guid.Parse(CropA));
        Assert.True(a.Ready);
        Assert.Equal(900, a.NObs);
        var b = result.Data.Crops.Single(c => c.CropId == Guid.Parse(CropB));
        Assert.False(b.Ready);
        Assert.Equal(120, b.NObs);
    }

    [Fact]
    public async Task Honest_empty_map_is_success_not_failure()
    {
        // No model registered / ML internal failure -> the service answers the
        // empty shape. That must reach the FE as a 200 (no tint), never a 400.
        var (handler, clientMock) = BuildHandler();
        clientMock.Setup(c => c.GetCropReadinessAsync(default)).ReturnsAsync(new CropReadinessDto
        {
            ModelVersion = null,
            MinHistoryObs = null,
            ModelActive = false,
        });

        var result = await handler.Handle(new GetCropReadinessQuery(), default);

        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.ModelActive);
        Assert.Empty(result.Data.Crops);
    }

    [Fact]
    public async Task Transport_failure_is_a_failure()
    {
        var (handler, clientMock) = BuildHandler();
        clientMock.Setup(c => c.GetCropReadinessAsync(default)).ReturnsAsync((CropReadinessDto?)null);

        var result = await handler.Handle(new GetCropReadinessQuery(), default);

        Assert.False(result.IsSuccess);
        Assert.Contains("unavailable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_guid_key_is_skipped_not_fatal()
    {
        var (handler, clientMock) = BuildHandler();
        clientMock.Setup(c => c.GetCropReadinessAsync(default)).ReturnsAsync(new CropReadinessDto
        {
            ModelActive = true,
            Crops = new Dictionary<string, CropReadinessEntryDto>
            {
                ["not-a-guid"] = new() { Ready = true, NObs = 1 },
                [CropA] = new() { Ready = true, NObs = 900 },
            },
        });

        var result = await handler.Handle(new GetCropReadinessQuery(), default);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Data!.Crops);
        Assert.Equal(Guid.Parse(CropA), item.CropId);
    }

    [Fact]
    public async Task Null_nobs_survives_as_null_never_zero()
    {
        // Old payloads have no per-crop n_obs; null means "unknown", and turning
        // it into 0 would tell the farmer "no data collected" — a fabrication.
        var (handler, clientMock) = BuildHandler();
        clientMock.Setup(c => c.GetCropReadinessAsync(default)).ReturnsAsync(new CropReadinessDto
        {
            ModelActive = true,
            Crops = new Dictionary<string, CropReadinessEntryDto>
            {
                [CropA] = new() { Ready = true, NObs = null },
            },
        });

        var result = await handler.Handle(new GetCropReadinessQuery(), default);

        Assert.Null(result.Data!.Crops.Single().NObs);
    }
}
