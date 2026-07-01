using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.EcconomicCenter.Commands.Create;

public class EcoCreateCommandHandler : IRequestHandler<EcoCreateCommand, Result<bool>>
{
    private readonly IEconimicCenterRepository _economicCenterRepository;
    private readonly ILogger<EcoCreateCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly CodeSettings _codeSettings;

    public EcoCreateCommandHandler(IEconimicCenterRepository economicCenterRepository, ILogger<EcoCreateCommandHandler> logger, IUnitofWorkRepository unitofWorkRepository, CodeSettings codeSettings)
    {
        _economicCenterRepository = economicCenterRepository;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _codeSettings = codeSettings;
    }
    public async Task<Result<bool>> Handle(EcoCreateCommand request, CancellationToken cancellationToken)
    {
        var dto = request.EcoCreateDto;
        if (dto is null)
        {
            _logger.LogInformation("Economic center details are required.");
            return Result<bool>.Failure("Economic center details are required.");
        }

        var EcoCode = await _codeSettings.GetEcoCode();
        if (string.IsNullOrEmpty(EcoCode))
        {
            _logger.LogError("Failed to generate economic center code.");
            return Result<bool>.Failure("Failed to generate economic center code.");
        }
        
        var economicCenter = dto.ToEntity();
        economicCenter.EcoCode = EcoCode;
        await _economicCenterRepository.Addasync(economicCenter);
        await _unitofWorkRepository.CommitAsync();
        _logger.LogInformation("Economic center details have been generated.Economic center code: {EcoCode}", EcoCode);
        return Result<bool>.Success(true);
    }
}