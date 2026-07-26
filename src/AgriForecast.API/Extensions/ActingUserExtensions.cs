using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Extensions;

/// <summary>
/// Resolves the authenticated caller's immutable user id from the JWT subject, for controllers that must
/// stamp WHO acted onto a command so the UserActivityLog audit trail can name the admin.
/// <para>
/// This is the ONE place that reads the identity, and it is always the JWT — never the request body or
/// route — so a caller cannot spoof "who am I" to misattribute an audit row or slip past an identity-based
/// guard such as the self-delete check. Default inbound claim mapping turns sub into
/// ClaimTypes.NameIdentifier, and the token generator sets NameIdentifier explicitly, so either is stable.
/// </para>
/// </summary>
public static class ActingUserExtensions
{
    /// <summary>The acting user's id, or null when the subject claim is missing or malformed (callers then return 401).</summary>
    public static Guid? GetActingUserId(this ControllerBase controller)
    {
        var sub = controller.User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? controller.User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
