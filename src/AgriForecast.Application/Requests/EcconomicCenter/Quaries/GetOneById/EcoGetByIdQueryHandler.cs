using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.EcconomicCenter.DTOs;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.EcconomicCenter.Quaries.GetOneById;

public class EcoGetByIdQueryHandler : IRequestHandler<EcoGetByIdQuery, Result<Eco_GetDto>>
{
    private readonly IEconimicCenterRepository _econimicCenterRepository;
    private readonly ILogger<EcoGetByIdQueryHandler> _loger;

    public EcoGetByIdQueryHandler(IEconimicCenterRepository econimicCenterRepository, ILogger<EcoGetByIdQueryHandler> loger)
    {
        _econimicCenterRepository = econimicCenterRepository;
        _loger = loger;
    }
    
    public async Task<Result<Eco_GetDto>> Handle(EcoGetByIdQuery request, CancellationToken cancellationToken)
    {
        var exsitingEco = await _econimicCenterRepository.GetByIdAsync(request.Guid);
        if (exsitingEco == null)
        {
            _loger.LogInformation("Economic center with ID {Id} does not exist.", request.Guid);
            return Result<Eco_GetDto>.Failure("Economic center does not exist");
        }
        
        var ecoDto = exsitingEco.ToGetDto();
        _loger.LogInformation("Economic center with ID {Id} retrieved successfully.", request.Guid);
        return Result<Eco_GetDto>.Success(ecoDto);
    }
}