using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace NxToWin.Updates;

internal static class GitHubUpdates
{
    internal const string PackageId = "NxToWin.Desktop";
    private const string Repository = "iCosiSenpai/nx-to-win";
    internal const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";

    private static readonly HttpClient Api = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    internal static Version CurrentVersion { get; } = Version.Parse(typeof(App).Assembly.GetName().Version!.ToString(3));

    internal static async Task<VelopackAsset?> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd($"NxToWin/{CurrentVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Api.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken).ConfigureAwait(false);
        return release is null ? throw new InvalidDataException("Missing release metadata.") : ReadAsset(release, CurrentVersion);
    }

    internal static VelopackAsset? ReadAsset(Release release, Version currentVersion)
    {
        if (release.Draft || release.Prerelease || release.TagName is not { } tag || !tag.StartsWith('v') ||
            !Version.TryParse(tag.AsSpan(1), out var version) || version.Build < 0 || version.Revision >= 0 ||
            !string.Equals(tag, $"v{version}", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid stable release version.");
        if (version <= currentVersion) return null;

        string fileName = $"{PackageId}-{version}-full.nupkg";
        var asset = release.Assets?.SingleOrDefault(candidate => candidate.Name == fileName);
        if (!release.Immutable || asset is not { State: "uploaded", Size: > 0, Digest: { Length: 71 } digest } ||
            !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("Expected an immutable Windows release with a SHA-256 asset digest.");

        return new VelopackAsset
        {
            PackageId = PackageId,
            Version = SemanticVersion.Parse(version.ToString()),
            Type = VelopackAssetType.Full,
            FileName = fileName,
            Size = asset.Size,
            // Velopack compares its uppercase hexadecimal digest with an ordinal comparison.
            SHA256 = digest[7..].ToUpperInvariant()
        };
    }

    internal sealed record Release(
        [property: JsonPropertyName("tag_name")] string? TagName, bool Draft, bool Prerelease, bool Immutable, Asset[]? Assets);

    internal sealed record Asset(string Name, string State, long Size, string? Digest);

    internal sealed class Manager(IVelopackLocator? locator = null) : UpdateManager(new Source(), locator: locator)
    {
        // The stock download path skips checksum verification when a package is already cached.
        internal Task VerifyAsync(VelopackAsset asset) => VerifyPackageChecksumAsync(asset);
    }

    private sealed class Source : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) =>
            throw new NotSupportedException("Use the immutable release metadata from the GitHub API.");

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry,
            string localFile, Action<int> progress, CancellationToken cancelToken = default)
        {
            string url = $"https://github.com/{Repository}/releases/download/v{releaseEntry.Version}/{releaseEntry.FileName}";
            return new HttpClientFileDownloader().DownloadFile(url, localFile, progress, null, 10, cancelToken);
        }
    }
}
