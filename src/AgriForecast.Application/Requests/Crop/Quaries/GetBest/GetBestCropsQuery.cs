using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Crop.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Crop.Quaries.GetBest;

public class GetBestCropsQuery : IRequest<Result<List<BestCrop_GetDto>>>
{
    public int LookbackMonths { get; set; } = 3;
}
