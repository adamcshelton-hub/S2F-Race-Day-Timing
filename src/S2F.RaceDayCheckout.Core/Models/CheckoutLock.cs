namespace S2F.RaceDayCheckout.Core.Models;

public enum LockLifecycleStatus
{
    Active,
    Released
}

public sealed class CheckoutLock
{
    public string EventPath { get; set; } = string.Empty;
    public string Office { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string ParentEvent { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string HolderName { get; set; } = string.Empty;
    public string WindowsUser { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public DateTimeOffset CheckedOutAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public LockLifecycleStatus Status { get; set; } = LockLifecycleStatus.Active;
    public string? ReclaimReason { get; set; }
    public List<string> ScopedPaths { get; set; } = new();
    public string? PrimaryEventFile { get; set; }
}

public sealed class LockHistoryEntry
{
    public DateTimeOffset At { get; set; }
    public string Action { get; set; } = string.Empty;
    public string HolderName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
