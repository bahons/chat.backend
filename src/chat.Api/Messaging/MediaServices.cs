using Amazon.Runtime;
using Amazon.S3;
using chat.Api.Middleware;
using chat.Application.Media;
using chat.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace chat.Api.Messaging;

internal sealed class ObjectStorageOptions
{
    public string? ServiceUrl { get; set; }
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "chat-media";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public bool CreateBucket { get; set; }
}

internal sealed class FileScanOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 3310;
}

internal static class MediaServices
{
    public static void AddConversationMedia(this IServiceCollection services, bool development)
    {
        services.AddOptions<ObjectStorageOptions>().BindConfiguration("ObjectStorage")
            .Validate(options => string.IsNullOrEmpty(options.ServiceUrl)
                || (Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var uri)
                    && (uri.Scheme == "https" || (development && uri.Scheme == "http"))
                    && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
                    && !string.IsNullOrWhiteSpace(options.Region) && !string.IsNullOrWhiteSpace(options.Bucket)
                    && !string.IsNullOrWhiteSpace(options.AccessKey) && !string.IsNullOrWhiteSpace(options.SecretKey)),
                "Invalid object storage configuration.")
            .ValidateOnStart();
        services.AddOptions<FileScanOptions>().BindConfiguration("FileScan")
            .Validate(options => options.Port is >= 1 and <= 65535, "Invalid scanner port.").ValidateOnStart();

        services.AddSingleton<IAmazonS3>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
            if (string.IsNullOrEmpty(options.ServiceUrl))
            {
                throw new ApiFailure(StatusCodes.Status503ServiceUnavailable);
            }

            return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), new AmazonS3Config
            {
                ServiceURL = options.ServiceUrl,
                AuthenticationRegion = options.Region,
                ForcePathStyle = true,
                Timeout = TimeSpan.FromSeconds(15),
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            });
        });
        services.AddScoped<IPrivateObjectStorage>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
            return new S3ObjectStorage(provider.GetRequiredService<IAmazonS3>(), options.Bucket, options.CreateBucket,
                new Uri(options.ServiceUrl!).Scheme == Uri.UriSchemeHttps);
        });
        services.AddSingleton<IFileScanner>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<FileScanOptions>>().Value;
            return new ClamAvFileScanner(options.Host, options.Port);
        });
        services.AddScoped<MediaService>();
    }
}
