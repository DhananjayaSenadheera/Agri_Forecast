namespace AgriForecast.Domain.Entities;

/// <summary>
/// A persisted server-side record of an issued refresh JWT, keyed by the token's <see cref="Jti"/>
/// (the JWT id claim). This is the state that upgrades the refresh flow from stateless to revocable:
/// a refresh token is honoured only when its jti row exists, is unexpired, is not revoked, and has
/// not already been used (rotated). Rows chain by <see cref="FamilyId"/> across rotations so a whole
/// lineage can be revoked at once (logout, admin delete/demote, and reuse-detection theft response).
///
/// SECURITY: the jti is a token identifier — never log it or the raw token. Log
/// <see cref="UserId"/> / <see cref="FamilyId"/> only.
/// </summary>
public class RefreshTokenRecord
{
    public Guid Id { get; set; }

    /// <summary>The JWT <c>jti</c> claim of the issued refresh token. Unique — the rotation lookup key.</summary>
    public Guid Jti { get; set; }

    /// <summary>
    /// Rotation lineage id. A login/register starts a new family; every rotation issues a child row
    /// carrying the SAME FamilyId. Revoking a family kills the whole session line in one operation.
    /// </summary>
    public Guid FamilyId { get; set; }

    /// <summary>Owning user. FK → Users with CASCADE delete (see DbContext config).</summary>
    public Guid UserId { get; set; }

    public DateTime IssuedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>When this token was rotated (consumed). Null = still current. A used token presented again is a reuse/theft signal.</summary>
    public DateTime? UsedAtUtc { get; set; }

    /// <summary>When this token (or its whole family) was revoked. Null = not revoked.</summary>
    public DateTime? RevokedAtUtc { get; set; }
}
