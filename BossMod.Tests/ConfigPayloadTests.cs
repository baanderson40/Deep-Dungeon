using System.Text.Json;
using Xunit;

namespace BossMod.Tests;

public class ConfigPayloadTests
{
    [Fact]
    public void DuplicateSectionsKeepLastValueAndUnknownSectionsSurviveSaveReload()
    {
        using var input = JsonDocument.Parse("""{"UMADConfig":{"value":1},"unknown":{"keep":[1,2]},"UMADConfig":{"value":2}}""");
        var duplicates = new List<string>();
        var properties = ConfigPayload.PreserveProperties(input.RootElement, duplicates.Add);
        Assert.Equal(new[] { "UMADConfig" }, duplicates);
        Assert.Equal(input.RootElement.GetProperty("UMADConfig").GetRawText(), properties["UMADConfig"].GetRawText());

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in properties)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var reloaded = JsonDocument.Parse(output.ToArray());
        Assert.Equal(2, reloaded.RootElement.EnumerateObject().Count());
        Assert.Equal(2, reloaded.RootElement.GetProperty("UMADConfig").GetProperty("value").GetInt32());
        Assert.Equal(2, reloaded.RootElement.GetProperty("unknown").GetProperty("keep").GetArrayLength());
    }

    [Fact]
    public void DistinctCaseSensitiveKeysAreNotDuplicates()
    {
        using var input = JsonDocument.Parse("""{"Config":1,"config":2}""");
        var properties = ConfigPayload.PreserveProperties(input.RootElement, _ => Assert.Fail("Unexpected duplicate"));
        Assert.Equal(2, properties.Count);
    }
}
