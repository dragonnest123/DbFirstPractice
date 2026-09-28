namespace Workflow;

/// <summary>Process-local counters published by a single worker replica.</summary>
public sealed class MetricsCounters
{
    private long _claimed;
    private long _completed;
    private long _failed;
    private long _leaseConflicts;

    public long Claimed => Interlocked.Read(ref _claimed);
    public long Completed => Interlocked.Read(ref _completed);
    public long Failed => Interlocked.Read(ref _failed);
    public long LeaseConflicts => Interlocked.Read(ref _leaseConflicts);

    public void RecordClaim() => Interlocked.Increment(ref _claimed);

    public void RecordCompletion() => Interlocked.Increment(ref _completed);

    public void RecordFailure() => Interlocked.Increment(ref _failed);

    public void RecordLeaseConflict() => Interlocked.Increment(ref _leaseConflicts);
}
