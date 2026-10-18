namespace S2F.RaceDayCheckout.Core.Models;

/// <summary>
/// Who is checking out. Timing takes the exclusive lock and the full pack.
/// Announcer never locks and only pulls the assigned .event plus DB's.
/// </summary>
public enum CheckoutRole
{
    Timing = 0,
    Announcer = 1
}
