using System.Security.Cryptography;
using System.Text.Json;

namespace neat;

/// <summary>
/// Where filter releases live on this machine: the one bundled next to neat.exe, and the ones
/// the updater downloaded (one folder per version under <see cref="Root"/>). No WinUI in here,
/// so the self-check can run it.
/// </summary>
internal sealed class FilterStore
{
    /// <summary>Releases kept in <see cref="Root"/> after a clean-up: the newest ones.</summary>
    public const int Keep = 2;

    public string Bundled { get; }
    public string Root { get; }

    public FilterStore(string bundled, string root)
    {
        Bundled = bundled;
        Root = root;
    }

    /// <summary>A version that is safe to use as a folder name (the manifest is not trusted to say where it goes).</summary>
    public static bool IsSafeVersion(string? v)
    {
        if (string.IsNullOrEmpty(v) || v.Length > 40)
            return false;

        foreach (var c in v)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
                return false;
        }

        return v[0] != '.';
    }

    /// <summary>Folders that hold a release, newest version first; unverified.</summary>
    public List<(string Dir, string Version)> Candidates()
    {
        var list = new List<(string, string)>();

        void Add(string dir)
        {
            if (Directory.Exists(dir) && FilterDir.ReadVersion(dir) is { Length: > 0 } v)
                list.Add((dir, v));
        }

        Add(Bundled);
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var d in Directory.GetDirectories(Root))
                {
                    // ".staging-..." and the like are unfinished downloads.
                    if (!Path.GetFileName(d).StartsWith('.'))
                        Add(d);
                }
            }
        }
        catch (Exception)
        {
            // an unreadable folder holds no release
        }

        // The same version in two places is the same release: the bundled one is preferred.
        return list.OrderByDescending(x => x.Item2, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The newest release whose files check out, or null when there is none. Releases that fail
    /// the check are skipped (and reported to <paramref name="log"/>), so a damaged download
    /// falls back to the next one, in the end to the bundled one.
    /// </summary>
    public (string Dir, string Version)? Best(Action<string>? log = null)
    {
        foreach (var (dir, version) in Candidates())
        {
            try
            {
                FilterDir.Verify(dir);
                return (dir, version);
            }
            catch (Exception ex)
            {
                log?.Invoke($"filters {version} in {dir} cannot be used: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>Removes downloaded releases but the newest <see cref="Keep"/> and the ones in <paramref name="inUse"/>. Never throws.</summary>
    public void CleanUp(IReadOnlyCollection<string> inUse, Action<string>? log = null)
    {
        try
        {
            if (!Directory.Exists(Root))
                return;

            var dirs = Directory.GetDirectories(Root)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .ToList();

            var kept = 0;
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                var used = inUse.Any(u => string.Equals(Path.GetFullPath(u), Path.GetFullPath(d), StringComparison.OrdinalIgnoreCase));

                // Unfinished downloads of an earlier run are not kept; a running one is not touched
                // (it is younger than a day).
                var staging = name.StartsWith('.');
                if (staging && Directory.GetLastWriteTimeUtc(d) > DateTime.UtcNow.AddDays(-1))
                    continue;

                if (!staging && (used || ++kept <= Keep))
                    continue;

                try
                {
                    Directory.Delete(d, true);
                    log?.Invoke("removed old filters " + name);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"could not remove {d}: {ex.Message}");
                }
            }
        }
        catch (Exception)
        {
            // clean-up is a courtesy
        }
    }
}

/// <summary>
/// Looks for a newer neat-filters release and downloads it into <see cref="FilterStore.Root"/>.
/// Files whose hash is already known locally are copied, not downloaded again (most lists do not
/// change between two releases). Every file is checked against the manifest before the release
/// becomes visible, and the release is moved into place only when all of it is there.
/// </summary>
internal sealed class FilterUpdater
{
    private const long MaxManifest = 2_000_000;
    private const long MaxFile = 80_000_000;
    private const long MaxTotal = 300_000_000;

    private readonly HttpClient _http;
    private readonly string _repo;
    private readonly FilterStore _store;
    private readonly Action<string> _log;

    /// <param name="repoUrl">
    /// The repository's address on GitHub ("https://github.com/owner/repo"). Only its ordinary web
    /// addresses are used (".../releases/latest" and ".../releases/download/tag/file"), never the API,
    /// whose limit of 60 requests an hour for each address is quickly used up on a shared network.
    /// </param>
    public FilterUpdater(HttpClient http, string repoUrl, FilterStore store, Action<string> log)
    {
        _http = http;
        _repo = repoUrl.TrimEnd('/');
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Returns the folder of a release newer than <paramref name="currentVersion"/> (downloading it
    /// when needed), or null when there is nothing newer. Throws when the release cannot be
    /// reached or does not check out; nothing half-done is left visible.
    /// </summary>
    public async Task<string?> CheckAsync(string currentVersion, CancellationToken ct)
    {
        // The release is pinned by its tag, so a release published while this runs cannot mix two of them.
        var tag = await LatestTag(ct);
        var baseUrl = $"{_repo}/releases/download/{Uri.EscapeDataString(tag)}/";

        var manifestBytes = await Download(baseUrl + "manifest.json", MaxManifest, ct);
        string version;
        List<(string Name, string Sha)> files;
        using (var doc = JsonDocument.Parse(manifestBytes))
        {
            var root = doc.RootElement;
            version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (!FilterStore.IsSafeVersion(version))
                throw new InvalidDataException("the release has no usable version");

            files = new List<(string, string)>();
            if (!root.TryGetProperty("files", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("the manifest lists no files");

            foreach (var f in arr.EnumerateArray())
            {
                var name = f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                var sha = f.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "";
                if (name.Length == 0 || name != Path.GetFileName(name) || name.StartsWith('.') || sha.Length != 64)
                    throw new InvalidDataException("bad file entry in the manifest: " + name);

                files.Add((name, sha.ToLowerInvariant()));
            }
        }

        if (string.CompareOrdinal(version, currentVersion) <= 0)
        {
            _log($"filters {currentVersion} are the newest (the release is {version})");
            return null;
        }

        var target = Path.Combine(_store.Root, version);
        if (Directory.Exists(target))
        {
            try
            {
                FilterDir.Verify(target);
                return target; // downloaded before, and not in use yet
            }
            catch (Exception)
            {
                Directory.Delete(target, true); // damaged: fetched again below
            }
        }

        Directory.CreateDirectory(_store.Root);
        var staging = Path.Combine(_store.Root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            // What can be copied from what is here already.
            var local = new List<(string Dir, Dictionary<string, string> Hashes)>();
            foreach (var (dir, _) in _store.Candidates())
                local.Add((dir, FilterDir.Hashes(dir)));

            long total = 0;
            var downloaded = 0;
            var copied = 0;
            foreach (var (name, sha) in files)
            {
                var dest = Path.Combine(staging, name);
                var source = local.FirstOrDefault(l => l.Hashes.TryGetValue(name, out var h) && h == sha && File.Exists(Path.Combine(l.Dir, name))).Dir;
                if (source is not null)
                {
                    File.Copy(Path.Combine(source, name), dest);
                    copied++;
                    continue;
                }

                total += await DownloadTo(baseUrl + Uri.EscapeDataString(name), dest, sha, MaxFile, ct);
                downloaded++;
                if (total > MaxTotal)
                    throw new InvalidDataException("the release is larger than expected");
            }

            await File.WriteAllBytesAsync(Path.Combine(staging, "manifest.json"), manifestBytes, ct);

            // Everything once more, as the app does at start: only a release that passes becomes visible.
            FilterDir.Verify(staging);
            Directory.Move(staging, target);
            _log($"filters {version} downloaded: {downloaded} files ({total / 1_000_000.0:0.#} MB), {copied} reused");
            return target;
        }
        catch
        {
            try
            {
                Directory.Delete(staging, true);
            }
            catch (Exception)
            {
                // CleanUp removes it later
            }

            throw;
        }
    }

    /// <summary>
    /// The tag of the latest release: ".../releases/latest" answers with a redirect to
    /// ".../releases/tag/TAG", which the client has followed by the time the answer arrives.
    /// </summary>
    private async Task<string> LatestTag(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _repo + "/releases/latest");
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidDataException("no release was found (is the repository public?): " + _repo);

        if (res.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
            throw new InvalidDataException("GitHub is limiting requests for now, trying again later");

        res.EnsureSuccessStatusCode();

        var path = res.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
        var i = path.LastIndexOf("/tag/", StringComparison.Ordinal);
        if (i < 0)
            throw new InvalidDataException("the latest release has no tag");

        var tag = Uri.UnescapeDataString(path[(i + 5)..]);
        if (!FilterStore.IsSafeVersion(tag))
            throw new InvalidDataException("the latest release has an unusable tag");

        return tag;
    }

    private async Task<byte[]> Download(string url, long max, CancellationToken ct)
    {
        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        return await ReadLimited(res, max, ct);
    }

    private static async Task<byte[]> ReadLimited(HttpResponseMessage res, long max, CancellationToken ct)
    {
        if (res.Content.Headers.ContentLength > max)
            throw new InvalidDataException("the answer is larger than expected");

        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > max)
                throw new InvalidDataException("the answer is larger than expected");
        }

        return ms.ToArray();
    }

    /// <summary>Downloads a file, checking its hash and size while it arrives. Returns its size.</summary>
    private async Task<long> DownloadTo(string url, string path, string wantSha, long max, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(5));

        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength > max)
            throw new InvalidDataException(Path.GetFileName(path) + " is larger than expected");

        using var sha = SHA256.Create();
        long size = 0;
        await using (var s = await res.Content.ReadAsStreamAsync(limit.Token))
        await using (var f = File.Create(path))
        {
            var buf = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(buf, limit.Token)) > 0)
            {
                size += n;
                if (size > max)
                    throw new InvalidDataException(Path.GetFileName(path) + " is larger than expected");

                sha.TransformBlock(buf, 0, n, null, 0);
                await f.WriteAsync(buf.AsMemory(0, n), limit.Token);
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var got = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (got != wantSha)
            throw new InvalidDataException(Path.GetFileName(path) + " does not match its hash");

        return size;
    }
}
