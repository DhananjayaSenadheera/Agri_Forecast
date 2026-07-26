namespace AgriForecast.Domain.Enums;

// The kind of security-relevant account event recorded on a UserActivityLog row (Logs hub PR A).
// Stored as int (HasConversion<int>); the numeric values are pinned by test and a reorder would
// silently corrupt persisted rows (the fix for a reorder belongs in a migration, never here).
//
// LoginSucceeded = a credential check passed and a token was issued (ActorUserId = the user).
// LoginFailed    = a credential check failed. UsernameAttempted carries the ATTEMPTED username (the
//                  security signal) — NEVER the password. No ActorUserId (the caller is unproven).
// UserRegistered = a new account was self-registered (ActorUserId = the new user's id).
// RoleChanged    = an admin changed a user's role (ActorUserId = acting admin, TargetUserId = target,
//                  Details = "role -> <newRole>").
// UserDeleted    = an admin deleted a user (ActorUserId = acting admin, TargetUserId = deleted id).
//
// ── CONTENT-AUDIT members (5..9) ────────────────────────────────────────────────────────────────
// One member per admin-mutable CONTENT entity (not one per verb): create/update/delete all share the
// entity's member and are told apart by Details ("created '<id>'" / "updated '<id>'" /
// "deleted '<id>'"). One-per-entity keeps the Logs-hub filter list short and human-readable, and a
// new verb never needs a new enum member + migration-visible int.
// For all five: ActorUserId = the acting admin (JWT sub), TargetUserId = NULL (these act on content,
// not on a user); the content's short identifier lives in Details, never a request body.
//
// PolicyFlagChanged = a policy flag was created/updated/deleted (Details carries its Title).
// FestivalChanged   = a festival-calendar entry was mutated (Details carries "<FestivalKey> yyyy-MM-dd").
// NewsEventChanged  = a news event was mutated (Details carries its Title).
// CropChanged       = a crop was mutated (Details carries its CropCode, or the id when unavailable).
// MarketChanged     = a market / economic centre was registered (Details carries its Name).
public enum UserActivityEventType
{
    LoginSucceeded = 0,
    LoginFailed = 1,
    UserRegistered = 2,
    RoleChanged = 3,
    UserDeleted = 4,
    PolicyFlagChanged = 5,
    FestivalChanged = 6,
    NewsEventChanged = 7,
    CropChanged = 8,
    MarketChanged = 9
}
