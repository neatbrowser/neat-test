using System.Runtime.InteropServices;

namespace neat;

/// <summary>
/// The C functions of neat_adblock.dll (the neat-adblock repository, include/neat_adblock.h).
/// Only ads/engine.cs calls these. The dll sits next to neat.exe; when it is missing the first
/// call throws DllNotFoundException, which ads/ads.cs turns into "blocker off".
/// </summary>
internal static class Native
{
    /// <summary>Must equal NEAT_ABI_VERSION in the dll.</summary>
    public const uint AbiVersion = 1;

    private const string Lib = "neat_adblock";

    [StructLayout(LayoutKind.Sequential)]
    public struct CheckResult
    {
        public int Action;          // 0 allow, 1 block, 2 redirect
        public IntPtr Redirect;     // redirect: the replacement resource, a data: URL
        public IntPtr RewrittenUrl; // allow: load this address instead ($removeparam)
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint neat_abi_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr neat_engine_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dir, out IntPtr error);

    /// <summary>Like neat_engine_create, but uses the engine cache at cachePath when it was written for the same key. Dll 0.2.0 and later.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr neat_engine_load(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string cachePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        out int fromCache,
        out IntPtr error);

    /// <summary>Writes the engine cache for key to path. 0 = written. Dll 0.2.0 and later.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int neat_engine_save(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        out IntPtr error);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void neat_engine_free(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void neat_string_free(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int neat_check_request(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceUrl,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string requestType,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? method,
        out CheckResult result);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void neat_check_result_free(ref CheckResult result);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int neat_check_popup(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string targetUrl,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string openerUrl);

    /// <summary>1 when a top-level navigation to the address is blocked ($document / $all rules). Newer dlls only.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int neat_check_document(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string url);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int neat_cosmetic_resources(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        out IntPtr json);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int neat_hidden_class_id_selectors(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string classesJson,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string idsJson,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string exceptionsJson,
        out IntPtr json);
}
