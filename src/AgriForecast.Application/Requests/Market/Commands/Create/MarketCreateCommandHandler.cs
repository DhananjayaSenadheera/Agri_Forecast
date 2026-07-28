using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using MarketEntity = AgriForecast.Domain.Entities.Market;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Market.Commands.Create;

// Registers a Market (a Dedicated Economic Centre when IsEconomicCenter=true): generate the business code
// via CodeSettings, stamp it once, persist, commit. Mirrors CropCreateCommandHandler.
public class MarketCreateCommandHandler : IRequestHandler<MarketCreateCommand, Result<bool>>
{
    private readonly CodeSettings _codeSettings;
    private readonly IGenericRepository<MarketEntity> _marketRepository;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly ILogger<MarketCreateCommandHandler> _logger;
    private readonly IUserActivityAudit _activityAudit;

    public MarketCreateCommandHandler(
        CodeSettings codeSettings,
        IGenericRepository<MarketEntity> marketRepository,
        IUnitofWorkRepository unitOfWork,
        ILogger<MarketCreateCommandHandler> logger,
        IUserActivityAudit activityAudit)
    {
        _codeSettings = codeSettings;
        _marketRepository = marketRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _activityAudit = activityAudit;
    }

    public async Task<Result<bool>> Handle(MarketCreateCommand request, CancellationToken cancellationToken)
    {
        var dto = request.CreateDto;
        if (dto is null)
        {
            _logger.LogInformation("Failed to create market: Market details are null.");
            return Result<bool>.Failure("Market details cannot be null.");
        }

        // Reject a duplicate display code here rather than letting the unique index throw: the DB
        // violation would surface as an opaque 500, this returns the structured error the admin can act on.
        var shortCode = MarketEntity.NormalizeShortCode(dto.ShortCode);
        if (shortCode.Length > 0)
        {
            var clash = await _marketRepository.GetOneAsyncInclude(m => m.ShortCode == shortCode);
            if (clash is not null)
            {
                _logger.LogInformation(
                    "Failed to create market: short code {ShortCode} is already in use.", shortCode);
                return Result<bool>.Failure($"Short code '{shortCode}' is already used by another market.");
            }
        }

        var marketCode = await _codeSettings.GetMktCode();
        if (string.IsNullOrEmpty(marketCode))
        {
            _logger.LogError("Failed to generate market code.");
            return Result<bool>.Failure("Failed to generate market code.");
        }

        var market = dto.ToEntity();
        market.AssignCode(marketCode);
        await _marketRepository.AddAsync(market);
        await _unitOfWork.CommitAsync();

        _logger.LogInformation(
            "Market created successfully with Market Code: {MarketCode} (IsEconomicCenter={IsEconomicCenter}).",
            marketCode, market.IsEconomicCenter);
        // Audited after the commit, and the audit swallows-and-logs, so it cannot fail the registration.
        await _activityAudit.RecordMarketChangedAsync(
            request.ActingUserId, ContentChangeAction.Created, market.Name, cancellationToken);

        return Result<bool>.Success(true);
    }
}
