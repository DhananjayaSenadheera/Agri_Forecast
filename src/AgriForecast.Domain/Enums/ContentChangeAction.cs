namespace AgriForecast.Domain.Enums;

/// <summary>
/// The verb of an admin content mutation, used only to render the Details note on an audit row.
/// It is not persisted and not part of any wire contract, so unlike UserActivityEventType its numeric
/// values are NOT pinned and may be reordered freely. An enum only so the wording cannot drift.
/// </summary>
public enum ContentChangeAction
{
    Created,
    Updated,
    Deleted
}
