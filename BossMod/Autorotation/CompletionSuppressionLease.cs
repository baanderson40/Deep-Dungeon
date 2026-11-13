namespace BossMod.Autorotation;

// A generation token proves ownership; an empty preset alone never does.
internal sealed class CompletionSuppressionLease
{
    private long _generation;
    private long _version;
    internal long Token { get; private set; }

    internal long Acquire(long version)
    {
        _version = version;
        return Token = ++_generation;
    }

    internal bool Owns(long token, long version, bool forceDisabled)
        => token != 0 && token == Token && version == _version && forceDisabled;

    internal bool ShouldClearOnCombatEnd(bool inCombat, bool forceDisabled, bool clearPreset, bool clearForceDisable, long version)
        => !inCombat && (forceDisabled ? clearForceDisable : clearPreset) && !Owns(Token, version, forceDisabled);

    internal bool Invalidate()
    {
        var owned = Token != 0;
        Token = 0;
        return owned;
    }

    internal bool Release(long token)
        => token != 0 && token == Token && Invalidate();
}
