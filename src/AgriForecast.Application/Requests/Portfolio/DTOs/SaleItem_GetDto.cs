namespace AgriForecast.Application.Requests.Portfolio.DTOs;

// One sale the farmer recorded. Returned by GET /api/portfolio/sales (inside the page envelope) and as the
// whole body of POST and PUT, so the UI can insert or update a row without refetching the list.
public class SaleItem_GetDto
{
    // The sale's own id — the handle for PUT /api/portfolio/sales/{id} and DELETE of the same.
    public Guid Id { get; set; }

    // IMMUTABLE after the row is created. A sale recorded against the wrong crop is deleted and re-added:
    // re-pointing it would silently re-attribute a reported price to a crop the farmer never named. PUT does
    // not accept a cropId at all.
    public Guid CropId { get; set; }

    public string CropName { get; set; } = string.Empty;

    // Display-only business code (VEG######/FRT######/DMB######). Never a join key — the ML side and every
    // FK key on the lowercase GUID CropId.
    public string? CropCode { get; set; }

    // Where the sale happened, when the farmer said. All three market fields are null TOGETHER when they
    // did not — a normal state, not missing data.
    public Guid? MarketId { get; set; }
    public string? MarketName { get; set; }

    // The short chip label (e.g. "DEC", "KEP"). Display-only and possibly empty — never a key.
    public string? MarketShortCode { get; set; }

    // yyyy-MM-dd. A date STRING rather than a DateTime: it has no time component and no timezone, and
    // shipping it as an instant is how a sale day becomes "the day before" for half the world.
    public string SaleDate { get; set; } = string.Empty;

    // LKR per kilo, as typed. decimal all the way down — money is never a float.
    public decimal PricePerKg { get; set; }

    // Kilos, or null when the farmer only remembered the price. Absent is a first-class answer here.
    public decimal? QuantityKg { get; set; }

    // The farmer's own free text, trimmed, at most UserSale.NoteMaxLength characters. Served ONLY to the
    // farmer who wrote it; it is never copied into the admin activity log.
    public string? Note { get; set; }

    // UTC instants, "Z"-stamped so the UI cannot read them as local time.
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
