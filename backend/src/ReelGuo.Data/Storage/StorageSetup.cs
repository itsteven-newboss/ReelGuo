using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ReelGuo.Data.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>"FileSystem" (development, tests) or "S3" (R2 / S3 / MinIO in production).</summary>
    public string Provider { get; set; } = "FileSystem";

    /// <summary>Filesystem root, relative to the app's content root. The API and the worker must point at the same folder.</summary>
    public string FileSystemRoot { get; set; } = "../../../.data/storage";

    public S3Options S3 { get; set; } = new();
}

public sealed class S3Options
{
    public string ServiceUrl { get; set; } = "";
    public string Region { get; set; } = "auto";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public bool ForcePathStyle { get; set; } = true;
    public string RawBucket { get; set; } = "reelguo-raw";
    public string MediaBucket { get; set; } = "reelguo-media";
}

public static class StorageSetup
{
    public static IServiceCollection AddReelGuoStorage(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        var options = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        services.AddSingleton(options);

        services.AddSingleton(_ =>
        {
            if (string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase))
            {
                var client = S3ObjectStorage.CreateClient(options.S3);
                return new StorageBuckets(
                    new S3ObjectStorage(client, options.S3.RawBucket),
                    new S3ObjectStorage(client, options.S3.MediaBucket));
            }

            var root = Path.GetFullPath(Path.Combine(contentRoot, options.FileSystemRoot));
            return new StorageBuckets(
                new FileSystemObjectStorage(Path.Combine(root, "raw")),
                new FileSystemObjectStorage(Path.Combine(root, "media")));
        });
        return services;
    }
}
