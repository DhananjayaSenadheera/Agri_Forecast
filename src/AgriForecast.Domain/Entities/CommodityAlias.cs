namespace AgriForecast.Domain.Entities;

// Maps a source's raw commodity label to a canonical Crop. Parsers must never hardcode aliases; they
// resolve the incoming label against the active rows here (e.g. HARTI "Ladies Fingers" -> "Lady's Fingers").
// Source = NULL means the alias applies to every source; otherwise it is scoped to that one source.
// Lookups rely on SQL Server's case-insensitive default collation, so "Beans" and "beans" are one
// alias — do not move this table to a case-sensitive collation.
public class CommodityAlias
{
    public Guid Id { get; private set; }

    // The raw label exactly as a source emits it (e.g. "Beans", "Bonchi", "Kohila").
    public string Alias { get; private set; } = string.Empty;

    // Required FK to Crops, OnDelete Restrict so a Crop cannot be deleted while an alias points at it.
    public Guid CropId { get; private set; }

    // NULL = applies to all sources; non-null scopes the alias to one source (e.g. "HARTI").
    public string? Source { get; private set; }

    // Optional language tag for the alias text, e.g. "en", "si-rom". Documentation only.
    public string? Language { get; private set; }

    // Soft-disable a stale mapping without deleting history (the mapping is version-controlled).
    public bool IsActive { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    private CommodityAlias() { }

    // Source and Language are optional; new aliases are active by default.
    public static CommodityAlias CreateNew(string alias, Guid cropId, string? source = null, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("Alias is required.", nameof(alias));
        if (cropId == Guid.Empty)
            throw new ArgumentException("CropId must be a non-empty Guid.", nameof(cropId));

        return new CommodityAlias
        {
            Id = Guid.NewGuid(),
            Alias = alias,
            CropId = cropId,
            Source = source,
            Language = language,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
