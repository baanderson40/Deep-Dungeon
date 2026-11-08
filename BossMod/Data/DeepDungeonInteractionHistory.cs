namespace BossMod;

// Bounded evidence of treasure interaction calls issued by HintExecutor. This is a diagnostic
// correlation aid, not confirmation that the server accepted the interaction.
public sealed class DeepDungeonInteractionHistory
{
    public readonly record struct TreasureCall(ulong ActorID, uint ObjectID, int DungeonID, byte Floor, DateTime IssuedAt, long Sequence);

    public const int Capacity = 32;
    public static readonly TimeSpan Retention = TimeSpan.FromSeconds(5);
    public const uint GoldCofferOID = 0x1EA13E;
    public const uint SilverCofferOID = 0x1EA13D;

    public static bool IsTreasureInteractionTarget(ActorType type, uint objectID)
        => type == ActorType.Treasure || objectID is GoldCofferOID or SilverCofferOID;

    private readonly Queue<TreasureCall> _calls = new();
    private long _nextSequence;

    public void RecordTreasureCall(ulong actorID, uint objectID, int dungeonID, byte floor, DateTime issuedAt)
    {
        if (actorID == 0 || dungeonID == 0 || floor == 0)
            return;

        _calls.Enqueue(new(actorID, objectID, dungeonID, floor, issuedAt, ++_nextSequence));
        while (_calls.Count > Capacity || _calls.TryPeek(out var oldest) && issuedAt >= oldest.IssuedAt && issuedAt - oldest.IssuedAt > Retention)
            _calls.Dequeue();
    }

    public TreasureCall[] Snapshot(DateTime currentTime)
    {
        while (_calls.TryPeek(out var oldest) && currentTime >= oldest.IssuedAt && currentTime - oldest.IssuedAt > Retention)
            _calls.Dequeue();
        return [.. _calls];
    }

    public void Clear() => _calls.Clear();
}
