namespace Maki.Data;

/// <summary>
/// Copies a deleted series' Full incognito state onto its activity before <c>ON DELETE SET NULL</c>
/// clears the link. SQLite drops the trigger when <c>Series</c> is rebuilt and refuses to rename a
/// rebuilt <c>StatsEvents</c> or <c>ReadingSessions</c> while it exists, so a migration that rebuilds
/// any of the three runs <see cref="Drop"/> first and <see cref="Create"/> after.
/// </summary>
public static class ActivityPrivacyTrigger
{
    public const string Create = """
        CREATE TRIGGER TR_Series_PreserveActivityPrivacy
        BEFORE DELETE ON Series
        BEGIN
            UPDATE StatsEvents SET HiddenAfterRemoval = (OLD.Incognito = 2) WHERE SeriesId = OLD.Id;
            UPDATE ReadingSessions SET HiddenAfterRemoval = (OLD.Incognito = 2) WHERE SeriesId = OLD.Id;
        END;
        """;

    public const string Drop = "DROP TRIGGER IF EXISTS TR_Series_PreserveActivityPrivacy;";
}
