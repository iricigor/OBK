using System.Globalization;
using System.Reflection;

namespace OBK.App;

public sealed record BuildIdentity(bool IsLocal, DateTimeOffset BuiltAt)
{
    public static BuildIdentity Current { get; } = ReadAssembly();

    public string TimestampText => BuiltAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    public string FullTimestampText => BuiltAt.ToString("O", CultureInfo.InvariantCulture);

    public static BuildIdentity FromMetadata(string? localBuild, string? timestamp)
    {
        if (!bool.TryParse(localBuild, out var isLocal))
            throw new InvalidOperationException("Missing or invalid LocalBuild assembly metadata.");
        if (timestamp is null ||
            !DateTimeOffset.TryParseExact(timestamp,
                ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz",
                 "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var builtAt))
            throw new InvalidOperationException("Missing or invalid BuildTimestamp assembly metadata. Use an ISO 8601 timestamp with a UTC Z or explicit offset.");
        return new(isLocal, builtAt.ToUniversalTime());
    }

    private static BuildIdentity ReadAssembly()
    {
        var metadata = typeof(BuildIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value);
        return FromMetadata(metadata.GetValueOrDefault("LocalBuild"), metadata.GetValueOrDefault("BuildTimestamp"));
    }
}
