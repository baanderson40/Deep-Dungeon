namespace BossMod;

// Admission requires adjacency; an admitted objective survives room-boundary crossings.
// The caller enforces the no-progress/deadline budgets and all current loot/control exclusions.
internal sealed class DeepDungeonTreasureSelection
{
    internal static readonly TimeSpan ObjectiveDeadline = TimeSpan.FromSeconds(60);
    private string? _context;
    private ulong _actor;
    private DateTime _started;

    internal bool CanRetain(string context, ulong actor)
        => actor != 0 && actor == _actor && context == _context;

    internal bool DeadlineExpired(string context, ulong actor, DateTime now)
        => CanRetain(context, actor) && (now < _started || now - _started >= ObjectiveDeadline);

    internal void Select(string context, ulong actor, DateTime now, bool adjacent)
    {
        if (actor == 0 || (!adjacent && !CanRetain(context, actor)))
        {
            Reset();
            return;
        }
        if (!CanRetain(context, actor))
        {
            _context = context;
            _actor = actor;
            _started = now; // neither adjacent nor retained samples refresh the objective deadline
        }
    }

    internal void Reset()
    {
        _context = null;
        _actor = 0;
        _started = default;
    }
}
