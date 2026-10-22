using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace neat;

/// <summary>
/// Something that can seal the vault's data key so that only the right person can open it
/// again. The vault uses <see cref="Dpapi"/>; the self-check uses a stand-in so it can run
/// on any machine.
/// </summary>
public interface IKeyWrap
{
    byte[] Protect(byte[] key);

    /// <summary>The key, or null when it cannot be opened here (another user, another machine).</summary>
    byte[]? Unprotect(byte[] blob);
}

/// <summary>
/// Windows DPAPI for the current user, the same protection Chrome gives its own key. What it
/// protects against: someone who copies the vault file, or the disk, to another account or
/// another machine. What it does not: a program running as you (it can ask DPAPI the same
/// question), so the vault never claims more than that.
///
/// It calls crypt32.dll directly. The managed wrapper (ProtectedData) is a separate package
/// on current .NET and the vault takes no extra packages.
/// </summary>
public sealed class Dpapi : IKeyWrap
{
    // Added to the key before it is sealed, so a block sealed by another program using DPAPI
    // for the current user does not open here, and the other way round.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("neat-vault/dpapi/1");

    // CRYPTPROTECT_UI_FORBIDDEN: never show a dialog.
    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        [MarshalAs(UnmanagedType.LPWStr)] string? description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    public Dpapi()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only on Windows.");
    }

    public byte[] Protect(byte[] key)
    {
        return Run(true, key) ?? throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public byte[]? Unprotect(byte[] blob)
    {
        return Run(false, blob);
    }

    private static byte[]? Run(bool protect, byte[] input)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);

        try
        {
            var dataIn = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };

            DataBlob dataOut;
            var ok = protect
                ? CryptProtectData(ref dataIn, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out dataOut);

            if (!ok)
                return null;

            try
            {
                var result = new byte[dataOut.Size];
                Marshal.Copy(dataOut.Data, result, 0, dataOut.Size);
                return result;
            }
            finally
            {
                // The memory Windows handed back may hold the key: overwrite it before freeing.
                Marshal.Copy(new byte[dataOut.Size], 0, dataOut.Data, dataOut.Size);
                LocalFree(dataOut.Data);
            }
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }
}
