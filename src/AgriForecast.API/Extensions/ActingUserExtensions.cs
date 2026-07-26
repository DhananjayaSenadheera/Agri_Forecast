using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Extensions;

/// <summary>
/// Resolves the authenticated caller's immutable user id from the JWT subject, for controllers that
/// must stamp WHO acted onto a command (user management + every admin content mutation, so the
/// UserActivityLog audit trail can name the admin).
/// <para>
/// This is the ONE place that reads the identity. It is always the JWT — never the request body or
/// route — so a caller cannot spoof "who am I" to misattribute an audit row or slip past an
/// identity-based guard (e.g. the self-delete check). Default inbound claim mapping turns
/// <c>sub</c> into <see cref="ClaimTypes.NameIdentifier"/>, and the token generator also sets
/// NameIdentifier explicitly, so reading either is stable.
/// </para>
/// Extracted from UserController's private copy when the content controllers needed the same
/// behaviour: five hand-copied claim readers would have been five chances to read the wrong claim.
/// </summary>
public static class ActingUserExtensions
{
    /// <summary>
    /// The acting user's id, or <c>null</c> when the subject claim is missing/malformed (callers
    /// return 401 rather than proceeding unattributed).
    /// </summary>
    public static Guid? GetActingUserId(this ControllerBase controller)
    {
        var sub = controller.User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? controller.User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
