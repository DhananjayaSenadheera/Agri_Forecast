using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// One row per time a farmer CLEARED the planting date they had recorded for a watched crop (table
// PlantedDateRemovals). Append-only: nothing rewrites or deletes a row, because the record of what happened
// to a planting is not editable after the fact.
//
// THIS IS A FIRST-CLASS RECORD, NOT AN AUDIT ROW. UserActivityLog is written fail-open, after the commit,
// and losing a row there costs a line in an admin log. This row is written INSIDE the same commit as the
// clear itself, so a cleared date can never be observable without the reason it was cleared. That is the
// whole point of the feature: "the date is gone" and "the crop was harvested" are one fact, and splitting
// them across two transactions would let the database hold a half-truth.
//
// RemovedPlantedDate is the date that WAS recorded — kept because it is the only surviving trace of that
// planting once the watchlist column is null, and because a "harvested" removal is meaningless without the
// day the crop went in.
//
// Privacy: UserId/CropId are ids, and Note is the farmer's own short free text. The note lives HERE and
// nowhere else — it is deliberately never copied into UserActivityLog.Details, which is code-authored text
// only. Rows are built through the factory below so the caps can never be bypassed. occurredUtc is passed
// in so tests are deterministic. Style precedent: UserActivityEvent.
public class PlantedDateRemoval
{
    // bigint identity; the table is append-only.
    public long Id { get; private set; }

    // The farmer whose planting date this was. FK -> Users (Cascade): personal data that does not outlive
    // its owner, exactly like the watchlist row it belongs to.
    public Guid UserId { get; private set; }

    // FK -> Crops (Restrict): the crop a removal refers to cannot be deleted out from under the record.
    public Guid CropId { get; private set; }

    // The planting date that was cleared. Date-only, no hidden time component.
    public DateOnly RemovedPlantedDate { get; private set; }

    public PlantedDateRemovalReason Reason { get; private set; }

    // The farmer's optional note. Trimmed; blank stores null rather than an empty string.
    public string? Note { get; private set; }

    // When the removal happened. Record-keeping only; never a feature.
    public DateTime OccurredUtc { get; private set; }

    /// <summary>
    /// nvarchar column cap for <see cref="Note"/>, mirrored by the EF configuration and by the wire
    /// validation, which REJECTS an over-long note rather than letting it reach the truncation below.
    /// </summary>
    public const int NoteMaxLength = 300;

    private PlantedDateRemoval() { }

    /// <summary>
    /// Records one removal. The reason is required by the type — there is no factory that omits it, which is
    /// the domain half of "a clear must never be observable without its reason".
    /// </summary>
    /// <remarks>
    /// The note is trimmed then capped, and a blank note stores null. Truncation here is defence in depth:
    /// the application layer answers an over-long note with the <c>clear_reason_note_too_long</c> wire code
    /// instead of silently shortening a farmer's own words.
    /// </remarks>
    public static PlantedDateRemoval Record(
        Guid userId,
        Guid cropId,
        DateOnly removedPlantedDate,
        PlantedDateRemovalReason reason,
        string? note,
        DateTime occurredUtc)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));

        if (cropId == Guid.Empty)
            throw new ArgumentException("CropId is required.", nameof(cropId));

        // 0001-01-01 is what an unset DateOnly looks like, and the whole reason this column exists is to keep
        // the ONE surviving trace of the planting that was cleared. A row claiming a crop went in in year 1
        // records nothing, so it is refused rather than stored. Unreachable in production — UserCropWatchlist
        // already refuses any PlantedDate before WatchlistLimits.EarliestPlantedDate, so no stored date can
        // be the default — which is exactly why it belongs here as a guard and not as a wire error code.
        if (removedPlantedDate == default)
            throw new ArgumentException(
                "RemovedPlantedDate is required.", nameof(removedPlantedDate));

        if (!Enum.IsDefined(reason))
            throw new ArgumentException(
                "Reason must be a defined PlantedDateRemovalReason.", nameof(reason));

        if (occurredUtc == default)
            throw new ArgumentException("OccurredUtc is required.", nameof(occurredUtc));

        return new PlantedDateRemoval
        {
            UserId = userId,
            CropId = cropId,
            RemovedPlantedDate = removedPlantedDate,
            Reason = reason,
            Note = Cap(note, NoteMaxLength),
            OccurredUtc = occurredUtc
        };
    }

    // Trim then cap to the column length; a blank value stores null rather than an empty string.
    private static string? Cap(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
