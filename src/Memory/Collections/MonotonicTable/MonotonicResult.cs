namespace Memory.Collections.MonotonicTable;

/// <summary>
/// Outcome of a sequence-guarded monotonic upsert operation.
/// </summary>
public enum MonotonicResult : byte
{
    /// <summary>Write rejected: Candidate sequence is strictly older than the stored sequence.</summary>
    Stale = 0,

    /// <summary>Write accepted: Key was absent; fresh entry inserted.</summary>
    Inserted = 1,

    /// <summary>Write accepted: Candidate sequence strictly dominates existing sequence.</summary>
    Advanced = 2,

    /// <summary>Write accepted: Candidate sequence matches existing sequence; value replaced.</summary>
    ReplacedAtSameSequence = 3,
}
