namespace AgriForecast.Domain.Enums;

// Why a farmer cleared the planting date they had recorded for a watched crop. Stored as int on
// PlantedDateRemovals — the numeric values are persisted, so never renumber or reorder them (fix a mistake
// in a migration instead), and new reasons are APPENDED.
//
// The set is deliberately small and closed: it is a picker on a farmer's phone, not a taxonomy. Harvested
// is the ordinary end of a planting; CropFailed is the honest bad outcome; EnteredByMistake is a correction,
// which must stay tellable apart from a real outcome or the removals table would read as crop history it is
// not; Other is the escape hatch, and the free-text note exists for it.
public enum PlantedDateRemovalReason
{
    Harvested = 0,
    CropFailed = 1,
    EnteredByMistake = 2,
    Other = 3
}
