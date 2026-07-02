using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.EcconomicCenter.Commands.Update;

public class EcoUpdateCommandHandler : IRequestHandler<EcoUpdateCommand, Result<bool>>
{
    private readonly IEconimicCenterRepository _econimicCenterRepository;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly ILogger<EcoUpdateCommandHandler> _logger;

    public EcoUpdateCommandHandler(IEconimicCenterRepository econimicCenterRepository, IUnitofWorkRepository unitOfWork, ILogger<EcoUpdateCommandHandler> logger)
    {
        _econimicCenterRepository = econimicCenterRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }
    
    public async Task<Result<bool>> Handle(EcoUpdateCommand request, CancellationToken cancellationToken)
    {
        var dto = request.EcoUpdateDto;
        if (dto is null)
        {
            _logger.LogWarning("No Eco Update found.");
            return Result<bool>.Failure("Economic center details are required.");
        }
        var existingEco = await _econimicCenterRepository.GetByIdAsync(dto.Id);
        if (existingEco == null)
        {
            _logger.LogWarning("Economic center with ID {Id} not found.", dto.Id);
            return Result<bool>.Failure("Economic center does not exist.");
        }
        var eco = dto.ApplyTo(existingEco);
        await _econimicCenterRepository.UpdateAsync(eco);
        await _unitOfWork.CommitAsync();
        _logger.LogInformation("Economic center with ID {Id} updated successfully.", dto.Id);
        return Result<bool>.Success(true);

    }
}