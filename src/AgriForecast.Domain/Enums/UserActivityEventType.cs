namespace AgriForecast.Domain.Enums;

// The kind of event recorded on a UserActivityLog row. Stored as int — the numeric values are persisted,
// so never renumber or reorder them (fix a mistake in a migration instead).
//
// 0..4 are account events. LoginFailed carries the attempted username (never a password) and has no
// ActorUserId, because the caller is unproven. RoleChanged and UserDeleted carry the acting admin as
// ActorUserId and the affected account as TargetUserId.
//
// 5..9 are content events: one member per admin-mutable entity, not one per verb — create, update and
// delete share the entity's member and are told apart by the Details note. For all five, ActorUserId is
// the acting admin, TargetUserId is null, and Details carries the content's short identifier.
//
// 10..11 are pipeline-control events: an admin started, or asked to stop, the ingestion service. They
// carry the acting admin as ActorUserId, no TargetUserId, and the pass's batchId in Details, so an
// operator can join a control action to the IngestionRuns rows it produced. StopRequested is named for
// what is actually knowable: the API signals cancellation, it does not witness the pass end.
//
// 12 is the first FARMER-authored event: the actor is the farmer themselves, not an admin. It carries the
// crop code and the reason word in Details and, like every other event, no free text the user typed — the
// farmer's own note about the removal lives only on the PlantedDateRemovals row.
//
// 13..15 are the farmer's SALES LOG, and unlike the content events above they are one member PER VERB
// rather than one per entity: an admin reading the Logs page needs to see that a sale was deleted without
// opening the row, and a shared "SaleChanged" would hide that behind a Details note. They carry the farmer
// as ActorUserId, no TargetUserId, and "<CropCode> · Rs <price>/kg · <date>" in Details — never the
// farmer's own note, which lives only on the UserSales row.
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
    MarketChanged = 9,
    IngestionServiceStarted = 10,
    IngestionServiceStopRequested = 11,
    PlantedDateRemoved = 12,
    SaleRecorded = 13,
    SaleUpdated = 14,
    SaleDeleted = 15
}
