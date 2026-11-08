using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using chat.Application.Media;

namespace chat.Infrastructure.Media;

public sealed class S3ObjectStorage(IAmazonS3 client, string bucket, bool createBucket, bool useHttps) : IPrivateObjectStorage
{
    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await client.DeleteObjectAsync(bucket, key, ct);

    public async Task<string> PresignUploadAsync(string key, string contentType, int size, DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        if (createBucket)
        {
            try
            {
                await client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket }, ct);
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                try
                {
                    await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, ct);
                }
                catch (AmazonS3Exception alreadyExists) when (alreadyExists.ErrorCode == "BucketAlreadyOwnedByYou")
                {
                    // Concurrent creation of the same private bucket.
                }
            }
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Expires = expiresAt.UtcDateTime,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Protocol = useHttps ? Protocol.HTTPS : Protocol.HTTP
        };
        request.Headers["Content-Length"] = size.ToString(CultureInfo.InvariantCulture);
        return await client.GetPreSignedURLAsync(request);
    }

    public async Task<byte[]?> ReadUploadAsync(string key, string contentType, int size, CancellationToken ct)
    {
        try
        {
            using var result = await client.GetObjectAsync(bucket, key, ct);
            if (result.ContentLength != size || result.Headers.ContentType != contentType)
            {
                return null;
            }

            var bytes = new byte[size];
            await result.ResponseStream.ReadExactlyAsync(bytes, ct);
            return bytes;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task SaveVerifiedAsync(string key, byte[] bytes, string contentType, CancellationToken ct)
    {
        await using var content = new MemoryStream(bytes, writable: false);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentType = contentType,
            InputStream = content,
            DisablePayloadSigning = false
        }, ct);
    }

    public async Task<string> PresignDownloadAsync(string key, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Expires = expiresAt.UtcDateTime,
            Verb = HttpVerb.GET,
            Protocol = useHttps ? Protocol.HTTPS : Protocol.HTTP
        };
        request.ResponseHeaderOverrides.ContentType = "application/octet-stream";
        request.ResponseHeaderOverrides.ContentDisposition = "attachment";
        request.ResponseHeaderOverrides.CacheControl = "private, no-store";
        return await client.GetPreSignedURLAsync(request);
    }
}
