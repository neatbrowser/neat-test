using System.Security.Cryptography;
using System.Text;

namespace neat;

/// <summary>How the data key is protected.</summary>
public enum VaultMode
{
    /// <summary>Sealed by Windows for this user. Opens by itself for whoever is signed in to Windows.</summary>
    Dpapi,

    /// <summary>Sealed with a key made from the NEAT password. Opens only with that password.</summary>
    Password,
}

/// <summary>What <see cref="Vault.Open"/> found.</summary>
public enum OpenResult
{
    /// <summary>The vault is open and ready to use.</summary>
    Ready,

    /// <summary>There was no vault file: a new, empty one was made and is open.</summary>
    New,

    /// <summary>The vault is password protected: call Unlock with the NEAT password.</summary>
    NeedsPassword,

    /// <summary>
    /// The file is fine but Windows will not open its key here: a different Windows user, or
    /// the Windows account was reset. The entries cannot be read. The file is left untouched.
    /// </summary>
    KeyLost,

    /// <summary>The file cannot be read as a vault. It is left untouched.</summary>
    Unreadable,

    /// <summary>The file was made by a newer NEAT. It is left untouched, not downgraded.</summary>
    TooNew,
}

/// <summary>The outcome of unlocking the vault or changing how it is protected.</summary>
public enum VaultResult
{
    Ok,

    /// <summary>The password is wrong.</summary>
    Wrong,

    /// <summary>Too many wrong passwords lately: try again after <see cref="Vault.RetryAfter"/>.</summary>
    Wait,

    /// <summary>Windows will not open the key (see <see cref="OpenResult.KeyLost"/>).</summary>
    KeyLost,

    /// <summary>The vault is not in a state where this can be done (not open, or not password protected).</summary>
    Unavailable,

    /// <summary>The new password is shorter than <see cref="Vault.MinPassword"/>.</summary>
    TooShort,

    /// <summary>
    /// An entry does not check out (it was changed outside NEAT), so the vault cannot be
    /// re-protected without losing it. Nothing was changed.
    /// </summary>
    Damaged,
}

/// <summary>
/// What the list of saved logins shows. It holds no password. The user name is here because a
/// list of logins is of no use without it; it is still sealed in the file.
/// </summary>
public sealed record LoginInfo(
    Guid Id,
    string Site,
    string User,
    DateTimeOffset Created,
    DateTimeOffset Changed,
    bool Damaged);

/// <summary>
/// Proof that the person just passed the gate (Windows Hello or the NEAT password). Only the
/// sign-in code makes one, and <see cref="Vault.Reveal"/> will not show a password without a
/// valid one. It runs out after the time it was made with.
/// </summary>
public sealed class Access
{
    private readonly Func<DateTime> _now;
    private DateTime _until;

    public Access(TimeSpan lifetime)
        : this(lifetime, () => DateTime.UtcNow)
    {
    }

    internal Access(TimeSpan lifetime, Func<DateTime> now)
    {
        _now = now;
        _until = now() + lifetime;
    }

    public bool Valid => _until > _now();

    /// <summary>
    /// Pushes the end out to <paramref name="lifetime"/> from now, because the person is still
    /// working with the vault (the time that counts is the time since the last action). An access
    /// that has already run out stays dead.
    /// </summary>
    public void Touch(TimeSpan lifetime)
    {
        if (Valid)
            _until = _now() + lifetime;
    }

    /// <summary>Ends it now (the machine locked, the person asked, NEAT sat idle).</summary>
    public void Revoke()
    {
        _until = DateTime.MinValue;
    }
}

/// <summary>
/// A secret handed out for a moment. Dispose it as soon as it has been used: that overwrites
/// the bytes. <see cref="Text"/> makes a .NET string, and a string cannot be overwritten, so
/// use it only for the last step (the page, the clipboard) and keep that string short-lived.
/// </summary>
public sealed class Secret : IDisposable
{
    private byte[]? _bytes;

    internal Secret(byte[] bytes)
    {
        _bytes = bytes;
    }

    public ReadOnlySpan<byte> Bytes => _bytes ?? throw new ObjectDisposedException(nameof(Secret));

    public string Text()
    {
        return Encoding.UTF8.GetString(Bytes);
    }

    public void Dispose()
    {
        if (_bytes is null)
            return;

        CryptographicOperations.ZeroMemory(_bytes);
        _bytes = null;
    }
}

// ---- what is written to vault.json ----

internal sealed class VaultFile
{
    public int Version { get; set; }

    /// <summary>"dpapi" or "password".</summary>
    public string Mode { get; set; } = "dpapi";

    /// <summary>The data key, sealed: by DPAPI, or with the password-derived key. Base64.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Password mode only: the salt of the password-derived key. Base64.</summary>
    public string? Salt { get; set; }

    /// <summary>Password mode only: how many rounds of PBKDF2 make the key.</summary>
    public int Iterations { get; set; }

    public List<VaultRow> Logins { get; set; } = new();
}

internal sealed class VaultRow
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The origin, in the clear: it is how a saved login is looked up for a page.</summary>
    public string Site { get; set; } = string.Empty;

    /// <summary>Sealed. Base64.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Sealed. Base64.</summary>
    public string Pass { get; set; } = string.Empty;

    public DateTimeOffset Created { get; set; }

    public DateTimeOffset Changed { get; set; }

    public VaultRow Copy()
    {
        return (VaultRow)MemberwiseClone();
    }
}
