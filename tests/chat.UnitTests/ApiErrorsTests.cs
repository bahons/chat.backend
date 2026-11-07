using chat.Api.Middleware;
using Xunit;

namespace chat.UnitTests;

public sealed class ApiErrorsTests
{
    [Theory]
    [InlineData(400, "VALIDATION_ERROR")]
    [InlineData(401, "UNAUTHENTICATED")]
    [InlineData(403, "FORBIDDEN")]
    [InlineData(404, "NOT_FOUND")]
    [InlineData(409, "CONFLICT")]
    [InlineData(429, "RATE_LIMITED")]
    [InlineData(500, "INTERNAL_ERROR")]
    [InlineData(503, "INTERNAL_ERROR")]
    public void MapsHttpStatusToContractCode(int status, string expected)
    {
        Assert.Equal(expected, ApiErrors.Describe(status).Code);
    }
}
