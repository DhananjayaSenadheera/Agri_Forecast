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
public enum UserActivityEventType
{
    LoginSucceeded = 0,
    LoginFailed = 1,
    UserRegistered = 2,
    RoleChanged = 3,
    UserDeleted = 4
}
