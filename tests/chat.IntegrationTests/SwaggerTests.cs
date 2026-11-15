using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace chat.IntegrationTests;

public sealed class SwaggerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DocumentationCanBeEnabledOutsideDevelopment(bool enabled)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Staging").UseSetting("Swagger:Enabled", enabled.ToString()));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var ui = await client.GetAsync("/swagger/index.html");
        using var schema = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound, ui.StatusCode);
        Assert.Equal(enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound, schema.StatusCode);
        if (enabled)
        {
            var html = await ui.Content.ReadAsStringAsync();
            Assert.Equal("text/html", ui.Content.Headers.ContentType?.MediaType);
            Assert.Contains("index.js", html);
            var configuration = await client.GetStringAsync("/swagger/index.js");
            Assert.Contains("../openapi/v1.json", configuration);
            Assert.Contains("\"persistAuthorization\":false", configuration);
            Assert.Contains("\"validatorUrl\":null", configuration);
            foreach (var asset in new[] { "swagger-ui.css", "swagger-ui-bundle.js" })
            {
                using var response = await client.GetAsync("/swagger/" + asset);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            var document = JsonNode.Parse(await schema.Content.ReadAsStringAsync())!;
            Assert.Equal("bearer", (string?)document["components"]?["securitySchemes"]?["Bearer"]?["scheme"]);
            Assert.NotNull(document["paths"]?["/v1/me"]?["get"]?["security"]);
            using var anonymous = await client.GetAsync("/v1/me");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
    }
}
