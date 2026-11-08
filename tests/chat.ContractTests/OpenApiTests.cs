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
        var paths = actual["paths"]!;

        Assert.Equal("0.5.0", (string?)actual["info"]?["version"]);
        Assert.Null(paths["/health/live"]?["get"]?["responses"]?["429"]);
        Assert.Null(paths["/health/ready"]?["get"]?["responses"]?["429"]);
        Assert.NotNull(paths["/health/ready"]?["get"]?["responses"]?["503"]);

        var login = paths["/v1/auth/login"]!["post"]!["responses"]!;
        Assert.NotNull(login["200"]);
        Assert.NotNull(login["429"]?["headers"]?["Retry-After"]);
        Assert.NotNull(login["503"]);

        var upload = paths["/v1/business-applications/drafts/{id}/documents"]!["post"]!;
        var header = Assert.Single(upload["parameters"]!.AsArray(),
            parameter => (string?)parameter?["name"] == "X-Application-Draft")!;
        Assert.True((bool)header["required"]!);
        Assert.Equal("header", (string?)header["in"]);
        Assert.Equal("binary", (string?)upload["requestBody"]?["content"]?["application/pdf"]?["schema"]?["format"]);
        Assert.All(new[] { "404", "409", "413", "415" }, status => Assert.NotNull(upload["responses"]?[status]));

        var decision = paths["/v1/admin/business-applications/{id}/decision"]!["post"]!;
        Assert.Contains("MEDIA_NOT_READY", (string?)decision["responses"]?["409"]?["description"]);
        Assert.NotNull(paths["/v1/businesses"]?["get"]?["security"]);
        Assert.NotNull(paths["/v1/conversations/{id}/messages"]?["post"]?["responses"]?["409"]);

        var schemas = actual["components"]!["schemas"]!;
        Assert.Equal(new[] { "PENDING", "READY", "QUARANTINED" },
            schemas["VerificationDocumentInfo"]!["properties"]!["scanStatus"]!["enum"]!.AsArray()
                .Select(value => (string?)value));
        Assert.Null(schemas["MessageView"]!["properties"]!["authorUserId"]);
        Assert.NotNull(schemas["ConversationView"]!["properties"]!["readState"]);

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
