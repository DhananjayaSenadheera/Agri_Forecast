using AgriForecast.Application.common;
using AgriForecast.Application.Requests.EcconomicCenter.DTOs;
using AgriForecast.Domain.Interfaces;
using AutoMapper;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.EcconomicCenter.Quaries.GetAll;

public class EcoGetAllQueryHandler : IRequestHandler<EcoGetAllQuery, Result<List<Eco_GetDto>>>
{
    private readonly IEconimicCenterRepository _econimicCenterRepository;
    private readonly IMapper _mapper;
    private readonly ILogger<EcoGetAllQueryHandler> _logger;

    public EcoGetAllQueryHandler(IEconimicCenterRepository econimicCenterRepository, IMapper mapper, ILogger<EcoGetAllQueryHandler> logger)
    {
        _econimicCenterRepository = econimicCenterRepository;
        _mapper = mapper;
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
        var ecoDtos = _mapper.Map<List<Eco_GetDto>>(ecoList);
        _logger.LogInformation("{Count} economic centers retrieved successfully.", ecoDtos.Count);
        return Result<List<Eco_GetDto>>.Success(ecoDtos);
    }
}