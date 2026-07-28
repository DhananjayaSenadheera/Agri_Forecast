using System.Text.Json.Serialization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistEntry;

/// <summary>
/// PUT /api/portfolio/watchlist/{cropId} { marketIds?, plantedDate? } — update ONE watched crop.
/// </summary>
/// <remarks>
/// Both fields are independently optional, and "omitted" is a THIRD state, not a synonym for null:
/// <list type="bullet">
/// <item><c>marketIds</c> present ⇒ FULL REPLACE of that crop's markets; <c>[]</c> clears them. Omitted (or
/// null) ⇒ the markets are left exactly as they are.</item>
/// <item><c>plantedDate</c> present with a value ⇒ set; present and null ⇒ CLEAR; omitted ⇒ unchanged.</item>
/// </list>
/// <para>
/// The tri-state for plantedDate is carried by <see cref="PlantedDateSpecified"/>, which System.Text.Json
/// sets simply by CALLING the setter — a JSON key that is present, even with a null value, marks it. That
/// is why the property has a hand-written setter rather than an auto-property: without it, "clear the date"
/// and "do not touch the date" would be the same request, and one of the two would be impossible to
/// express. marketIds needs no such flag because an empty array already spells "clear" unambiguously.
/// </para>
/// <para>
/// A crop the caller does not watch is a 404, whether the row does not exist at all or belongs to another
/// farmer. UserId is stamped from the JWT and CropId from the route, so neither can be redirected by the
/// body.
/// </para>
/// </remarks>
public class UpdateWatchlistEntryCommand : IRequest<Result<WatchlistEntryUpdate_ResultDto>>
{
    private DateOnly? _plantedDate;

    // Set from the JWT, never bound from the body or route — see PortfolioController.
    public Guid UserId { get; set; }

    // From the route.
    public Guid CropId { get; set; }

    /// <summary>
    /// The complete new set of markets for this crop, or null/omitted to leave them unchanged. An empty
    /// array is meaningful: it clears every market. Duplicates are collapsed and the cap is counted after
    /// the collapse.
    /// </summary>
    public List<Guid>? MarketIds { get; set; }

    /// <summary>
    /// The farmer's planting day, null to clear it, or omitted to leave it unchanged.
    /// Assigning this property — including assigning null — marks it as specified.
    /// </summary>
    public DateOnly? PlantedDate
    {
        get => _plantedDate;
        set
        {
            _plantedDate = value;
            PlantedDateSpecified = true;
        }
    }

    /// <summary>
    /// True when the request actually carried a <c>plantedDate</c> key. Never bound from the wire — a
    /// caller cannot set it directly, it is a side effect of the setter above.
    /// </summary>
    [JsonIgnore]
    public bool PlantedDateSpecified { get; private set; }
}
