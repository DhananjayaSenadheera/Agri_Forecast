using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.FestivalCalendar.Quaries.GetAll;

public class FestivalCalendarGetAllQueryHandler
    : IRequestHandler<FestivalCalendarGetAllQuery, Result<List<FestivalCalendar_GetDto>>>
{
    private readonly IFestivalCalendarRepository _festivalCalendarRepository;
    private readonly ILogger<FestivalCalendarGetAllQueryHandler> _logger;

    public FestivalCalendarGetAllQueryHandler(
        IFestivalCalendarRepository festivalCalendarRepository,
        ILogger<FestivalCalendarGetAllQueryHandler> logger)
    {
        _festivalCalendarRepository = festivalCalendarRepository;
        _logger = logger;
    }

    public async Task<Result<List<FestivalCalendar_GetDto>>> Handle(
        FestivalCalendarGetAllQuery request, CancellationToken cancellationToken)
    {
        var entries = await _festivalCalendarRepository.GetAllAsync();

        // Empty calendar is a normal state → 200 [] (deliberately NOT the legacy
        // policy-flag 400-on-empty quirk; the admin Festivals page renders an empty list).
        var dtos = (entries ?? Enumerable.Empty<Domain.Entities.FestivalCalendarEntry>()).ToGetDtoList();
        _logger.LogInformation("{Count} festival calendar entries retrieved successfully.", dtos.Count);
        return Result<List<FestivalCalendar_GetDto>>.Success(dtos);
    }
}
