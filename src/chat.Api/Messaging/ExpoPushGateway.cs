using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using chat.Application.Push;
using Microsoft.Extensions.Options;

namespace chat.Api.Messaging;

internal sealed class PushOptions
{
    public bool Enabled { get; set; }
    public string? AccessToken { get; set; }
}

internal sealed class ExpoPushGateway(HttpClient http, IOptions<PushOptions> options) : IPushGateway
{
    public Task<PushResult> SendAsync(string token, Guid eventId, Guid conversationId, CancellationToken ct) =>
        RequestAsync("push/send", new
        {
            to = token,
            title = "chat",
            body = "New message",
            data = new { eventId, conversationId }
        }, null, ct);

    public Task<PushResult> CheckAsync(string ticketId, CancellationToken ct) =>
        RequestAsync("push/getReceipts", new { ids = new[] { ticketId } }, ticketId, ct);

    private async Task<PushResult> RequestAsync(string path, object payload, string? ticketId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        if (options.Value.AccessToken is { Length: > 0 } accessToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        {
            return new PushResult(PushResultKind.Retry);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new PushResult(PushResultKind.Rejected);
        }

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (!document.TryGetProperty("data", out var result) || result.ValueKind != JsonValueKind.Object
            || (ticketId is not null && !result.TryGetProperty(ticketId, out result)))
        {
            return new PushResult(PushResultKind.Retry);
        }

        if (result.TryGetProperty("status", out var status) && status.GetString() == "ok")
        {
            if (ticketId is not null)
            {
                return new PushResult(PushResultKind.Delivered);
            }

            return result.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 and <= 100 } acceptedId
                ? new PushResult(PushResultKind.Accepted, acceptedId)
                : new PushResult(PushResultKind.Retry);
        }

        var error = result.TryGetProperty("details", out var details) && details.TryGetProperty("error", out var value)
            ? value.GetString() : null;
        return new PushResult(error switch
        {
            "DeviceNotRegistered" => PushResultKind.Unregistered,
            "MessageRateExceeded" => PushResultKind.Retry,
            _ => PushResultKind.Rejected
        });
    }
}
