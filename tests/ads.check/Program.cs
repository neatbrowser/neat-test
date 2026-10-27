using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using neat;

// Self-check of the ad blocker's plain-code pieces. Plain asserts, no test package: one line per
// check, exit code 1 if any failed.

internal static class Program
{
    private static int _ran;
    private static int _failed;

    private static int Main()
    {
        Run("only http and https addresses are judged", WebOnly);
        Run("host as a script sees it", Hosts);
        Run("data: URLs are read", DataUrls);
        Run("request types from WebView2 contexts", ReqTypes);
        Run("cosmetic JSON is read, junk gives nothing", CosmeticJson);
        Run("page script gets its words filled in", PageScript);
        Run("a filter folder is checked against its manifest", FilterFolder);
        Run("a newer release is downloaded, checked and chosen", Updater);
        Run("version names and clean-up of old releases", Store);

        Console.WriteLine(_failed == 0 ? $"{_ran} checks passed" : $"{_failed} of {_ran} checks FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action check)
    {
        _ran++;
        try
        {
            check();
            Console.WriteLine("ok   " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL " + name + ": " + ex.Message);
        }
    }

    private static void Is<T>(T want, T got, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(want, got))
            throw new Exception($"{what}: wanted {want}, got {got}");
    }

    private static void WebOnly()
    {
        Is(true, AdUrl.IsWeb("https://a.test/x"), "https");
        Is(true, AdUrl.IsWeb("HTTP://a.test/"), "HTTP upper case");
        Is(false, AdUrl.IsWeb("data:text/plain,hi"), "data");
        Is(false, AdUrl.IsWeb("about:blank"), "about");
        Is(false, AdUrl.IsWeb(null), "null");
    }

    private static void Hosts()
    {
        Is(true, AdUrl.Covers("example.com", "example.com"), "same host is covered");
        Is(true, AdUrl.Covers("example.com", "WWW.Example.com"), "a subdomain is covered, any case");
        Is(false, AdUrl.Covers("example.com", "badexample.com"), "a longer name that only ends the same is not covered");
        Is(false, AdUrl.Covers("www.example.com", "example.com"), "a parent is not covered by its subdomain");
        Is(false, AdUrl.Covers("", "example.com"), "an empty entry covers nothing");
        Is<string?>("example.com", AdUrl.Host("https://Example.COM:8080/a?b#c"), "plain host");
        Is<string?>("xn--bcher-kva.example", AdUrl.Host("https://bücher.example/"), "idn host is punycode");
        Is<string?>(null, AdUrl.Host("file:///c:/x"), "file");
        Is("https://a.test/p?q", AdUrl.NoHash("https://a.test/p?q#frag"), "no hash");
    }

    private static void DataUrls()
    {
        Is(true, DataUrl.TryParse("data:text/javascript;base64,KCgpPT57fSkoKQ==", out var mime, out var body), "base64 parses");
        Is("text/javascript", mime, "base64 type");
        Is("(()=>{})()", Encoding.UTF8.GetString(body), "base64 body");

        Is(true, DataUrl.TryParse("data:text/plain;charset=utf-8,a%20b", out mime, out body), "plain parses");
        Is("text/plain", mime, "plain type");
        Is("a b", Encoding.UTF8.GetString(body), "plain body");

        Is(true, DataUrl.TryParse("data:,x", out mime, out body), "no type parses");
        Is("text/plain", mime, "default type");

        Is(false, DataUrl.TryParse("https://a.test/", out _, out _), "not data");
        Is(false, DataUrl.TryParse("data:image/gif;base64,!!!", out _, out _), "bad base64");
        Is(false, DataUrl.TryParse(null, out _, out _), "null");
    }

    private static void ReqTypes()
    {
        Is("document", ReqType.From("Document", true), "page");
        Is("subdocument", ReqType.From("Document", false), "frame");
        Is("xmlhttprequest", ReqType.From("Fetch", false), "fetch");
        Is("xmlhttprequest", ReqType.From("XmlHttpRequest", false), "xhr");
        Is("script", ReqType.From("Script", false), "script");
        Is("ping", ReqType.From("CspViolationReport", false), "report");
        Is("other", ReqType.From("TextTrack", false), "unknown");
    }

    private static void CosmeticJson()
    {
        var c = Cosmetic.Parse("""
            {"hide_selectors":[".ad","","#b"],"procedural_actions":["{}","{}"],"exceptions":[".x"],
             "injected_script":"var a=1;","generichide":true}
            """);
        Is(2, c.HideSelectors.Length, "selectors (empty one dropped)");
        Is(".x", c.Exceptions[0], "exception");
        Is("var a=1;", c.InjectedScript, "script");
        Is(true, c.GenericHide, "generichide");
        Is(2, c.Procedural, "procedural count");
        Is("{}", c.ProceduralActions[0], "procedural text");

        Is(0, Cosmetic.Parse("not json").HideSelectors.Length, "junk");
        Is(0, Cosmetic.Parse(null).HideSelectors.Length, "null");
        Is(0, Cosmetic.Parse("{}").HideSelectors.Length, "empty object");
    }

    private static void PageScript()
    {
        var t = "A __NEAT_HOST__ B __NEAT_TOKEN__ C __NEAT_HIDE__ D __NEAT_GENERIC__ E /*__NEAT_SCRIPTLETS__*/ F";
        var s = PageJs.Build(t, "a.test", "tok", new[] { ".a", "b\"c" }, true, "x(\"__NEAT_HOST__\");");
        Is("A \"a.test\" B \"tok\" C [\".a\",\"b\\u0022c\"] D true E x(\"__NEAT_HOST__\"); F", s, "filled text");

        var p = PageJs.Build("P __NEAT_PROC__ Q", "a.test", "tok", Array.Empty<string>(), false, "", new[] { "{\"selector\":[]}" });
        Is("P [\"{\\u0022selector\\u0022:[]}\"] Q", p, "procedural rules filled in");
        Is("P [] Q", PageJs.Build("P __NEAT_PROC__ Q", "a.test", "tok", Array.Empty<string>(), false, ""), "no procedural rules");
    }

    private static void FilterFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "adscheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var list = "||ads.test^\n";
            File.WriteAllText(Path.Combine(dir, "l.txt"), list);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(list))).ToLowerInvariant();

            string Manifest(string name, string sha) =>
                $$"""{"version":"2026.1.1","files":[{"name":"{{name}}","kind":"list","sha256":"{{sha}}"}]}""";

            File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest("l.txt", hash));
            Is("2026.1.1", FilterDir.Verify(dir), "good folder");

            Throws(() => { File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest("l.txt", new string('0', 64))); FilterDir.Verify(dir); }, "wrong hash");
            Throws(() => { File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest("sub/l.txt", hash)); FilterDir.Verify(dir); }, "path in name");
            Throws(() => { File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest("gone.txt", hash)); FilterDir.Verify(dir); }, "missing file");
            Throws(() => { File.Delete(Path.Combine(dir, "manifest.json")); FilterDir.Verify(dir); }, "no manifest");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- list updates ----

    private sealed class Fake : HttpMessageHandler
    {
        public const string Repo = "https://gh.test/o/r";

        public readonly Dictionary<string, byte[]> Pages = new();
        public readonly List<string> Asked = new();

        /// <summary>The tag ".../releases/latest" leads to; null means the repository has no release.</summary>
        public string? Tag;

        /// <summary>When set, every request is answered with this status (a rate limit, say).</summary>
        public HttpStatusCode? Always;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();
            Asked.Add(url);

            if (Always is { } code)
                return Task.FromResult(new HttpResponseMessage(code));

            // What the client sees after following GitHub's redirect from /releases/latest to /releases/tag/TAG.
            if (url == Repo + "/releases/latest")
            {
                return Task.FromResult(Tag is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, Repo + "/releases/tag/" + Tag),
                        Content = new StringContent("<html></html>"),
                    });
            }

            return Task.FromResult(Pages.TryGetValue(url, out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] ManifestJson(string version, params (string Name, string Text)[] files) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            version,
            files = files.Select(f => new { name = f.Name, kind = "list", sha256 = Sha(Encoding.UTF8.GetBytes(f.Text)) }).ToArray(),
        });

    private static void WriteRelease(string dir, string version, params (string Name, string Text)[] files)
    {
        Directory.CreateDirectory(dir);
        foreach (var (n, t) in files)
            File.WriteAllText(Path.Combine(dir, n), t);

        File.WriteAllBytes(Path.Combine(dir, "manifest.json"), ManifestJson(version, files));
    }

    /// <summary>Makes the fake server publish a release under a tag (the manifest and the files).</summary>
    private static void Serve(Fake f, string tag, string version, params (string Name, string Text)[] files)
    {
        f.Tag = tag;
        var dl = Fake.Repo + "/releases/download/" + tag + "/";
        f.Pages[dl + "manifest.json"] = ManifestJson(version, files);
        foreach (var (n, t) in files)
            f.Pages[dl + n] = Encoding.UTF8.GetBytes(t);
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "adscheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Updater()
    {
        var tmp = TempDir();
        try
        {
            var bundled = Path.Combine(tmp, "filters");
            var root = Path.Combine(tmp, "dl");
            WriteRelease(bundled, "2026.1.1.0000", ("a.txt", "||a.test^\n"), ("b.txt", "||b.test^\n"));
            var store = new FilterStore(bundled, root);
            Is("2026.1.1.0000", store.Best()!.Value.Version, "only the bundled release exists");

            var fake = new Fake();
            using var http = new HttpClient(fake);
            var up = new FilterUpdater(http, Fake.Repo, store, _ => { });

            Serve(fake, "v2", "2026.1.2.0000", ("a.txt", "||a.test^\n"), ("c.txt", "||c.test^\n"));
            var got = up.CheckAsync("2026.1.1.0000", default).GetAwaiter().GetResult();
            Is<string?>(Path.Combine(root, "2026.1.2.0000"), got, "the new release's folder");
            Is("||c.test^\n", File.ReadAllText(Path.Combine(got!, "c.txt")), "downloaded file");
            Is("||a.test^\n", File.ReadAllText(Path.Combine(got!, "a.txt")), "reused file");
            Is(false, fake.Asked.Any(u => u.EndsWith("/a.txt")), "a list that did not change is not downloaded again");
            Is("2026.1.2.0000", store.Best()!.Value.Version, "the downloaded release is the best one now");
            Is(0, Directory.GetDirectories(root).Count(d => Path.GetFileName(d).StartsWith('.')), "no unfinished folder is left");

            Is<string?>(null, up.CheckAsync("2026.1.2.0000", default).GetAwaiter().GetResult(), "nothing newer than the one in use");
            Is<string?>(null, up.CheckAsync("2026.2.0.0000", default).GetAwaiter().GetResult(), "an older release is never taken");

            // A file that arrives altered is refused, and nothing of the release stays behind.
            Serve(fake, "v3", "2026.1.3.0000", ("c.txt", "||c3.test^\n"));
            fake.Pages[Fake.Repo + "/releases/download/v3/c.txt"] = Encoding.UTF8.GetBytes("||evil.test^\n");
            Throws(() => up.CheckAsync("2026.1.2.0000", default).GetAwaiter().GetResult(), "altered download");
            Is(false, Directory.Exists(Path.Combine(root, "2026.1.3.0000")), "the refused release is not visible");
            Is(0, Directory.GetDirectories(root).Count(d => Path.GetFileName(d).StartsWith('.')), "a refused download leaves no unfinished folder");

            // A version that tries to leave the folder is refused.
            Serve(fake, "v4", "../evil", ("c.txt", "||c.test^\n"));
            Throws(() => up.CheckAsync("2026.1.2.0000", default).GetAwaiter().GetResult(), "unsafe version");

            // A tag that would leave the download address is refused.
            Serve(fake, "v5;x", "2026.1.5.0000", ("c.txt", "||c.test^\n"));
            Throws(() => up.CheckAsync("2026.1.2.0000", default).GetAwaiter().GetResult(), "unsafe tag");

            // A repository with no release, and GitHub limiting requests: both end as a clear failure.
            fake.Tag = null;
            Throws(() => up.CheckAsync("2026.1.2.0000", default).GetAwaiter().GetResult(), "release not found");
            fake.Always = HttpStatusCode.Forbidden;
            Throws(() => up.CheckAsync("2026.1.2.0000", default).GetAwaiter().GetResult(), "rate limit");
            fake.Always = null;

            // A damaged download is skipped: the bundled release is used again.
            File.WriteAllText(Path.Combine(root, "2026.1.2.0000", "c.txt"), "damaged");
            Is("2026.1.1.0000", store.Best()!.Value.Version, "a damaged download falls back to the bundled release");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    private static void Store()
    {
        Is(true, FilterStore.IsSafeVersion("2026.10.26.0456"), "usual version");
        Is(false, FilterStore.IsSafeVersion("../x"), "path in version");
        Is(false, FilterStore.IsSafeVersion("a/b"), "slash in version");
        Is(false, FilterStore.IsSafeVersion(".hidden"), "leading dot");
        Is(false, FilterStore.IsSafeVersion(""), "empty version");
        Is(false, FilterStore.IsSafeVersion(new string('1', 41)), "long version");

        var tmp = TempDir();
        try
        {
            var root = Path.Combine(tmp, "dl");
            foreach (var v in new[] { "2025.1.1.0000", "2025.1.2.0000", "2026.1.1.0000", "2026.1.2.0000" })
                WriteRelease(Path.Combine(root, v), v, ("a.txt", "||" + v + ".test^\n"));

            var store = new FilterStore(Path.Combine(tmp, "none"), root);
            store.CleanUp(new[] { Path.Combine(root, "2025.1.1.0000") });

            var left = Directory.GetDirectories(root).Select(d => Path.GetFileName(d)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Is("2025.1.1.0000,2026.1.1.0000,2026.1.2.0000", string.Join(",", left!), "the newest two and the one in use stay");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    private static void Throws(Action a, string what)
    {
        try
        {
            a();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new Exception(what + ": should have been refused");
    }
}
