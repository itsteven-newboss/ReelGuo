using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace ReelGuo.Data.Storage;

/// <summary>
/// S3-compatible storage (Cloudflare R2, AWS S3, MinIO, Garage). Browser uploads use presigned PUT URLs,
/// so large files never pass through the API.
/// </summary>
public sealed class S3ObjectStorage(IAmazonS3 client, string bucket) : IObjectStorage
{
    public static IAmazonS3 CreateClient(S3Options o)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = o.ServiceUrl,
            ForcePathStyle = o.ForcePathStyle,
            AuthenticationRegion = o.Region,
            // Non-AWS providers reject the SDK v4 default checksum headers on some requests.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        return new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), config);
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        using var transfer = new TransferUtility(client);
        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = bucket,
            Key = StorageKeys.Require(key),
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, cancellationToken);
    }

    public async Task PutFileAsync(string key, string localPath, string contentType, CancellationToken cancellationToken)
    {
        using var transfer = new TransferUtility(client);
        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = bucket,
            Key = StorageKeys.Require(key),
            FilePath = localPath,
            ContentType = contentType,
        }, cancellationToken);
    }

    public async Task<StoredObject?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectAsync(bucket, StorageKeys.Require(key), cancellationToken);
            return new StoredObject(response.ResponseStream, response.ContentLength, response.Headers.ContentType);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetObjectMetadataAsync(bucket, StorageKeys.Require(key), cancellationToken);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task DownloadToFileAsync(string key, string localPath, CancellationToken cancellationToken)
    {
        using var transfer = new TransferUtility(client);
        await transfer.DownloadAsync(new TransferUtilityDownloadRequest
        {
            BucketName = bucket,
            Key = StorageKeys.Require(key),
            FilePath = localPath,
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await client.ListObjectsV2Async(request, cancellationToken);
            keys.AddRange((response.S3Objects ?? []).Select(o => o.Key));
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
        return keys;
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        var keys = await ListAsync(prefix, cancellationToken);
        foreach (var batch in keys.Chunk(1000))
        {
            await client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = batch.Select(k => new KeyVersion { Key = k }).ToList(),
            }, cancellationToken);
        }
    }

    public Uri? CreateUploadUrl(string key, string contentType, TimeSpan validFor) =>
        new(client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = StorageKeys.Require(key),
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(validFor),
            ContentType = contentType,
        }));
}
