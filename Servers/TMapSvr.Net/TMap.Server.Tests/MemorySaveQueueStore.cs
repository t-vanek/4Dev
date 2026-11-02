using TMap.Data;

namespace TMap.Server.Tests;

// Test double only. Production always constructs CharacterSaveQueueStore backed by SQL Server.
internal sealed class MemorySaveQueueState
{
    public readonly SortedDictionary<long, StoredCharacterSave> Rows = new();
    public long Sequence;
    public string? Identity;
    public bool Owned;
}
internal sealed class MemorySaveQueueStore(MemorySaveQueueState? state = null) : ICharacterSaveQueueStore
{
    public MemorySaveQueueState State { get; } = state ?? new();
    public bool FailAppend;
    public bool LoseLease;
    private bool _owned;
    public void Open(string identity)
    {
        if (State.Owned) throw new IOException("SQL ownership conflict");
        if (State.Identity is not null && State.Identity != identity) throw new InvalidDataException("SQL database identity mismatch");
        if (State.Identity is null && State.Rows.Count != 0) throw new InvalidDataException("Missing SQL owner identity");
        State.Identity = identity; State.Owned = true; _owned = true;
    }
    public void AssertLease() { if (!_owned || LoseLease) throw new IOException("SQL lease lost"); }
    public long Boundary { get { AssertLease(); return State.Rows.Count == 0 ? 0 : State.Rows.Keys.Max(); } }
    public int Count { get { AssertLease(); return State.Rows.Count; } }
    public long Append(Guid id, string checksum, string payload)
    {
        AssertLease(); if (FailAppend) throw new IOException("SQL enqueue unavailable");
        long seq = ++State.Sequence; State.Rows.Add(seq, new(seq, checksum, payload)); return seq;
    }
    public IReadOnlyList<StoredCharacterSave> Read(long boundary)
    { AssertLease(); return State.Rows.Values.Where(r => r.Sequence <= boundary).ToArray(); }
    public void Acknowledge(long sequence) { AssertLease(); State.Rows.Remove(sequence); }
    public void Dispose() { if (_owned) State.Owned = false; _owned = false; }
}
