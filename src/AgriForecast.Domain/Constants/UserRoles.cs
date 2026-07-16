namespace AgriForecast.Domain.Constants;

/// <summary>
/// The closed set of authorization roles. Role is a plain STRING column on <c>User</c> (there is
/// no role enum), so these constants are the single source of truth shared by the JWT role claim,
/// the <c>[Authorize(Roles = ...)]</c> attributes, the update-role whitelist, and the admin
/// bootstrap. Keep this set closed and fail-closed: anything outside <see cref="Assignable"/> is
/// rejected by the update-role command. Comparisons that touch stored data are done
/// case-insensitively so a data drift can never bypass a guard.
/// </summary>
public static class UserRoles
{
    public const string Admin = "Admin";
    public const string Farmer = "Farmer";

    /// <summary>
    /// Roles an admin may assign via the user-management endpoint. Deliberately excludes any
    /// future/privileged role until one is explicitly designed — new roles are opt-in here.
    /// </summary>
    public static readonly IReadOnlyCollection<string> Assignable = new[] { Admin, Farmer };

    /// <summary>True if <paramref name="role"/> is an assignable role (exact, case-sensitive match).</summary>
    public static bool IsAssignable(string? role) =>
        role is not null && Assignable.Contains(role);
}
