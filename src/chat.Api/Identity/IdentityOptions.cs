namespace chat.Api.Identity;

internal sealed class IdentityOptions
{
    public int AccessMinutes { get; set; } = 10;
    public int RefreshDays { get; set; } = 30;
    public int AuthRequestsPerMinute { get; set; } = 12;
    public int LoginAttemptsPerAccount { get; set; } = 8;
    public int LoginAttemptsPerAddress { get; set; } = 10;
    public int LoginWindowSeconds { get; set; } = 60;
    public int LoginKeyCapacity { get; set; } = 4096;
}

internal sealed class AccountEmailOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1025;
    public string From { get; set; } = "accounts@chat.local";
    public bool EnableTls { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? QueueKeyBase64 { get; set; }
    public int QueueCapacity { get; set; } = 1024;
}
