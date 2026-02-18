using AgriForecast.Application.common;
using AgriForecast.Domain.Interfaces;
using AutoMapper;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.EcconomicCenter.Commands.Delete;

public class EcoDeleteCommandHandler : IRequestHandler<EcoDeleteCommand, Result<bool>>
{
    private readonly IEconimicCenterRepository _econimicCenterRepository;
    private readonly IMapper _mapper;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly ILogger <EcoDeleteCommandHandler> _logger;

    public EcoDeleteCommandHandler(IEconimicCenterRepository econimicCenterRepository, IMapper mapper, IUnitofWorkRepository unitofWorkRepository, ILogger<EcoDeleteCommandHandler> logger)
    {
        _econimicCenterRepository = econimicCenterRepository;
        _mapper = mapper;
        _unitofWorkRepository = unitofWorkRepository;
        _logger = logger;
    }

    public async Task<Result<bool>> Handle(EcoDeleteCommand request, CancellationToken cancellationToken)
    {
        var existingEco = await _econimicCenterRepository.GetByIdAsync(request.Id);
        if (existingEco == null)
        {
            _logger.LogInformation("Economic center with ID {Id} does not exist.", request.Id);
            return Result<bool>.Failure("Economic center does not exist");
        }

        await _econimicCenterRepository.DeleteAsync(existingEco);
        await _unitofWorkRepository.CommitAsync();
        _logger.LogInformation("Economic center with ID {Id} has been deleted successfully.", request.Id);
        return Result<bool>.Success(true);
    }
}

