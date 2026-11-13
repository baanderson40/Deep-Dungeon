using System.Text.Json;

namespace BossMod;

internal static class ConfigPayload
{
    // JsonElement.TryGetProperty uses the last duplicate property; preserve that behavior on save.
    internal static Dictionary<string, JsonElement> PreserveProperties(JsonElement payload, Action<string> onDuplicate)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (result.ContainsKey(property.Name))
                onDuplicate(property.Name);
            result[property.Name] = property.Value;
        }
        return result;
    }
}
