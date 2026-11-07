using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace chat.ContractTests;

public sealed class OpenApiTests
{
    [Fact]
    public async Task OpenApiMatchesPublishedArtifact()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var document = await client.GetStringAsync("/openapi/v1.json");
        var actual = JsonNode.Parse(document)!;

        Assert.Equal("0.1.0", (string?)actual["info"]?["version"]);
        Assert.NotNull(actual["paths"]?["/health/live"]?["get"]?["responses"]?["200"]);
        Assert.NotNull(actual["paths"]?["/health/ready"]?["get"]?["responses"]?["503"]);

        var exportPath = Environment.GetEnvironmentVariable("CHAT_OPENAPI_EXPORT");
        if (!string.IsNullOrEmpty(exportPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(exportPath))!);
            await File.WriteAllTextAsync(exportPath, document + "\n");
            return;
        }

        var artifact = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "openapi.json"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(artifact), actual), "Regenerate artifacts/openapi.json.");
    }
}
