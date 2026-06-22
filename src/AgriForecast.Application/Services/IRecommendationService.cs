using AgriForecast.Application.Requests.Crop.DTOs;

namespace AgriForecast.Application.Services;

public interface IRecommendationService
{
    Task<List<BestCrop_GetDto>> GetBestCropsAsync(int lookbackMonths, CancellationToken ct = default);
}
