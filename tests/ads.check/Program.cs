using System.Security.Cryptography;
using System.Text;
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
