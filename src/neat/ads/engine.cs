using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace neat;

/// <summary>What the engine says about one request.</summary>
internal enum AdAction { Allow = 0, Block = 1, Redirect = 2 }

/// <param name="Redirect">Redirect: the resource to serve instead, a data: URL.</param>
/// <param name="Rewritten">Allow: load this address instead. Not used yet.</param>
internal readonly record struct Verdict(AdAction Action, string? Redirect, string? Rewritten)
{
    public static Verdict Allow { get; } = new(AdAction.Allow, null, null);
}

/// <summary>
/// One loaded filter release (a neat-filters release folder), behind the C functions of
/// neat_adblock.dll. Safe to use from any thread. Dispose can run while another thread is inside a
/// call: the native engine is freed only after the last call has returned.
/// </summary>
internal sealed class AdsEngine : IDisposable
{
    private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Handle() : base(true) { }

        protected override bool ReleaseHandle()
        {
            Native.neat_engine_free(handle);
            return true;
        }

        public void Set(IntPtr p) => SetHandle(p);
    }

    private readonly Handle _h;

    private AdsEngine(Handle h) => _h = h;

    /// <summary>
    /// Builds the engine from a folder holding manifest.json and the files it lists. Throws
    /// DllNotFoundException when the dll is missing and InvalidOperationException when it
    /// refuses the folder. The caller has checked the files' hashes.
    /// </summary>
    public static AdsEngine Create(string dir)
    {
        CheckAbi();
        var p = Native.neat_engine_create(dir, out var err);
        if (p == IntPtr.Zero)
            throw new InvalidOperationException(Take(err) ?? "neat_adblock could not build the engine");

        return Wrap(p);
    }

    /// <summary>
    /// Like <see cref="Create"/>, but takes the engine the dll saved earlier (see <see cref="SaveCache"/>)
    /// when it was saved for the same <paramref name="key"/>, which is much quicker than reading the
    /// lists. A missing, damaged or foreign cache is not an error: the lists are read instead.
    /// With a dll that predates the cache this is just <see cref="Create"/>.
    /// </summary>
    public static AdsEngine Load(string dir, string cachePath, string key, out bool fromCache)
    {
        fromCache = false;
        CheckAbi();

        IntPtr p;
        int used;
        IntPtr err;
        try
        {
            p = Native.neat_engine_load(dir, cachePath, key, out used, out err);
        }
        catch (EntryPointNotFoundException)
        {
            return Create(dir);
        }

        if (p == IntPtr.Zero)
            throw new InvalidOperationException(Take(err) ?? "neat_adblock could not build the engine");

        fromCache = used == 1;
        return Wrap(p);
    }

    /// <summary>
    /// Saves the engine so the next start can skip reading the lists. Returns false when the dll
    /// cannot (too old, disk error); nothing else depends on it.
    /// </summary>
    public bool SaveCache(string path, string key)
    {
        var added = false;
        try
        {
            _h.DangerousAddRef(ref added);
            var rc = Native.neat_engine_save(_h.DangerousGetHandle(), path, key, out var err);
            Take(err); // gives the message back to the dll
            return rc == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (added)
                _h.DangerousRelease();
        }
    }

    private static void CheckAbi()
    {
        var abi = Native.neat_abi_version();
        if (abi != Native.AbiVersion)
            throw new InvalidOperationException($"neat_adblock.dll has ABI {abi}, this build needs {Native.AbiVersion}");
    }

    private static AdsEngine Wrap(IntPtr p)
    {
        var h = new Handle();
        h.Set(p);
        return new AdsEngine(h);
    }

    public void Dispose() => _h.Dispose();

    /// <summary>A request that cannot be judged (bad address, internal error) is allowed.</summary>
    public Verdict Check(string url, string source, string type, string? method)
    {
        var added = false;
        try
        {
            _h.DangerousAddRef(ref added);
            var rc = Native.neat_check_request(_h.DangerousGetHandle(), url, source, type, method, out var r);
            try
            {
                if (rc != 0)
                    return Verdict.Allow;

                return new Verdict(
                    (AdAction)r.Action,
                    r.Redirect == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(r.Redirect),
                    r.RewrittenUrl == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(r.RewrittenUrl));
            }
            finally
            {
                Native.neat_check_result_free(ref r);
            }
        }
        finally
        {
            if (added)
                _h.DangerousRelease();
        }
    }

    /// <summary>True when a window the page opened to <paramref name="target"/> should be stopped.</summary>
    public bool BlockPopup(string target, string opener)
    {
        var added = false;
        try
        {
            _h.DangerousAddRef(ref added);
            return Native.neat_check_popup(_h.DangerousGetHandle(), target, opener) == 1;
        }
        finally
        {
            if (added)
                _h.DangerousRelease();
        }
    }

    /// <summary>
    /// True when the page itself, opened at <paramref name="url"/>, should be stopped (a rule with
    /// $document, $doc or $all). Throws EntryPointNotFoundException with a dll that predates it.
    /// </summary>
    public bool BlockDocument(string url)
    {
        var added = false;
        try
        {
            _h.DangerousAddRef(ref added);
            return Native.neat_check_document(_h.DangerousGetHandle(), url) == 1;
        }
        finally
        {
            if (added)
                _h.DangerousRelease();
        }
    }

    /// <summary>The cosmetic resources for a page, as the JSON the dll returns, or null.</summary>
    public string? CosmeticJson(string url)
    {
        var added = false;
        try
        {
            _h.DangerousAddRef(ref added);
            var rc = Native.neat_cosmetic_resources(_h.DangerousGetHandle(), url, out var json);
            var text = Take(json);
            return rc == 0 ? text : null;
        }
        finally
        {
            if (added)
                _h.DangerousRelease();
        }
    }

    /// <summary>Generic hide selectors for classes and ids seen on a page, as a JSON array, or null.</summary>
    public string? GenericJson(string classesJson, string idsJson, string exceptionsJson)
    {
        var added = false;
        try
        {
            _h.DangerousAddRef(ref added);
            var rc = Native.neat_hidden_class_id_selectors(
                _h.DangerousGetHandle(), classesJson, idsJson, exceptionsJson, out var json);
            var text = Take(json);
            return rc == 0 ? text : null;
        }
        finally
        {
            if (added)
                _h.DangerousRelease();
        }
    }

    /// <summary>Copies a string the dll allocated and gives it back to the dll.</summary>
    private static string? Take(IntPtr p)
    {
        if (p == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(p);
        }
        finally
        {
            Native.neat_string_free(p);
        }
    }
}
