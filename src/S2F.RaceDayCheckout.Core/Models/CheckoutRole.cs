namespace S2F.RaceDayCheckout.Core.Models;

/// <summary>
/// Who is checking out. Timing takes the exclusive lock and the full pack.
/// Announcer never locks and only pulls the assigned .event plus DB's.
/// ManualBackup writes Graph Manual Backup taps only (no Timing lock / no Play).
/// </summary>
public enum CheckoutRole
{
    Timing = 0,
    Announcer = 1,
    ManualBackup = 2
}

