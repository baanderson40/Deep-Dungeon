namespace BossMod.Global.DeepDungeon;

internal enum HistoricalTrapInputState
{
    Unavailable,
    Error,
    NullResponse,
    Empty,
    FilteredOut,
    Available,
}

internal readonly record struct HistoricalTrapInput(
    HistoricalTrapInputState State,
    ImmutableArray<Vector3> Locations,
    int Returned,
    int Invalid,
    int Problematic)
{
    public int Accepted => Locations.Length;
}

internal readonly record struct FloorTrapInput(List<Vector3> Locations, int FilteredOut);

internal readonly record struct HistoricalTrapFetch(string Provider, IEnumerable<Vector3>? Response, Exception? Error, bool Unavailable);

internal static class PalantirTrapData
{
    internal static HistoricalTrapFetch Fetch(Func<IEnumerable<Vector3>?> preferred, Func<IEnumerable<Vector3>?> legacy, Func<Exception, bool> isUnavailable)
    {
        try
        {
            return new("Palantir.GetTraps", preferred(), null, false);
        }
        catch (Exception error) when (isUnavailable(error))
        {
            try
            {
                return new("Palantir.Traps (legacy)", legacy(), null, false);
            }
            catch (Exception legacyError)
            {
                return new("Palantir.Traps (legacy)", null, legacyError, isUnavailable(legacyError));
            }
        }
        catch (Exception error)
        {
            return new("Palantir.GetTraps", null, error, isUnavailable(error));
        }
    }

    internal static HistoricalTrapInput Process(IEnumerable<Vector3>? response, Func<Vector3, bool> isProblematic)
    {
        if (response == null)
            return new(HistoricalTrapInputState.NullResponse, [], 0, 0, 0);

        var locations = response.ToArray();
        var accepted = new List<Vector3>(locations.Length);
        var invalid = 0;
        var problematic = 0;
        foreach (var location in locations)
        {
            if (!float.IsFinite(location.X) || !float.IsFinite(location.Y) || !float.IsFinite(location.Z))
            {
                ++invalid;
                continue;
            }
            if (isProblematic(location))
            {
                ++problematic;
                continue;
            }
            accepted.Add(location);
        }

        var state = locations.Length == 0 ? HistoricalTrapInputState.Empty
            : accepted.Count == 0 ? HistoricalTrapInputState.FilteredOut
            : HistoricalTrapInputState.Available;
        return new(state, [.. accepted], locations.Length, invalid, problematic);
    }

    internal static List<Vector3> FilterForFloor(IEnumerable<Vector3> locations, Func<Vector3, bool> belongsToFloor)
        => [.. locations.Where(belongsToFloor)];

    internal static FloorTrapInput FilterFloor(IEnumerable<Vector3> locations, Func<Vector3, bool> belongsToFloor)
    {
        var accepted = new List<Vector3>();
        var filteredOut = 0;
        foreach (var location in locations)
        {
            if (belongsToFloor(location))
                accepted.Add(location);
            else
                ++filteredOut;
        }
        return new(accepted, filteredOut);
    }

    internal static bool ShouldLogProviderState(string? previousKey, string currentKey, bool isError, DateTime lastErrorLogAt, DateTime now)
        => previousKey != currentKey || isError && (lastErrorLogAt == default || now - lastErrorLogAt >= TimeSpan.FromSeconds(30));

    internal static string ErrorSummary(Exception error)
    {
        var message = error.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length <= 160 ? message : message[..160];
    }
}
