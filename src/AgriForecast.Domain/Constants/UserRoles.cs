namespace AgriForecast.Domain.Constants;

/// <summary>
/// The role strings shared by the JWT role claim, the [Authorize(Roles = ...)] attributes, the
/// update-role whitelist and the admin bootstrap. Role is a plain string column, so anything outside
/// Assignable is rejected; comparisons against stored data are case-insensitive.
/// </summary>
public static class UserRoles
{
    public const string Admin = "Admin";
    public const string Farmer = "Farmer";

    /// <summary>Roles an admin may assign via the user-management endpoint.</summary>
    public static readonly IReadOnlyCollection<string> Assignable = new[] { Admin, Farmer };

    /// <summary>True if <paramref name="role"/> is an assignable role (exact, case-sensitive match).</summary>
    public static bool IsAssignable(string? role) =>
        role is not null && Assignable.Contains(role);
}
