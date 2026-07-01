using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.EcconomicCenter.DTOs;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.EcconomicCenter.Quaries.GetAll;

public class EcoGetAllQueryHandler : IRequestHandler<EcoGetAllQuery, Result<List<Eco_GetDto>>>
{
    private readonly IEconimicCenterRepository _econimicCenterRepository;
    private readonly ILogger<EcoGetAllQueryHandler> _logger;

    public EcoGetAllQueryHandler(IEconimicCenterRepository econimicCenterRepository, ILogger<EcoGetAllQueryHandler> logger)
    {
        _econimicCenterRepository = econimicCenterRepository;
        _logger = logger;
    }


    public async Task<Result<List<Eco_GetDto>>> Handle(EcoGetAllQuery request, CancellationToken cancellationToken)
    {
        var ecoList = await _econimicCenterRepository.GetAllAsync();
        if (ecoList == null || !ecoList.Any())
        {
            _logger.LogInformation("No economic centers found.");
            return Result<List<Eco_GetDto>>.Failure("No economic centers found.");
        }
        var ecoDtos = ecoList.ToGetDtoList();
        _logger.LogInformation("{Count} economic centers retrieved successfully.", ecoDtos.Count);
        return Result<List<Eco_GetDto>>.Success(ecoDtos);
    }
}