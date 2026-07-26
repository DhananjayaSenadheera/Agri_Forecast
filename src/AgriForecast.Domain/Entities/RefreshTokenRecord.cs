namespace AgriForecast.Domain.Entities;

/// <summary>
/// Server-side record of an issued refresh JWT, keyed by its jti. A refresh token is honoured only when
/// its row exists, is unexpired, is not revoked and has not already been used. Rows chain by FamilyId
/// across rotations so a whole session line can be revoked at once.
/// <para>Security: the jti identifies a token — never log it or the raw token; log UserId/FamilyId.</para>
/// </summary>
public class RefreshTokenRecord
{
    public Guid Id { get; set; }

    /// <summary>The JWT <c>jti</c> claim of the issued refresh token. Unique — the rotation lookup key.</summary>
    public Guid Jti { get; set; }

    /// <summary>Rotation lineage id: a login starts a family and every rotation reuses it, so revoking a family kills the whole session line.</summary>
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
