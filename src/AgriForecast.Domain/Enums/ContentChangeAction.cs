namespace AgriForecast.Domain.Enums;

/// <summary>
/// The verb of an admin CONTENT mutation, used ONLY to render the <c>Details</c> note on a
/// content-audit <c>UserActivityLog</c> row ("created '<c>X</c>'" / "updated '<c>X</c>'" /
/// "deleted '<c>X</c>'").
/// <para>
/// NOT PERSISTED as a column and NOT part of any wire contract — the persisted discriminator is
/// <see cref="UserActivityEventType"/> (one member per entity kind), and the verb lives inside the
/// free-text Details. It is an enum rather than a free string purely so the three notes cannot drift
/// into "delete"/"Deleted"/"removed" across thirteen call sites; consequently its numeric values are
/// NOT pinned and it may be reordered freely.
/// </para>
/// </summary>
public enum ContentChangeAction
{
    Created,
    Updated,
    Deleted
}
