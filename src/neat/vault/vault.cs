using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace neat;

/// <summary>
/// The password vault: sealed storage for saved logins. It has no screen of its own and it
/// knows nothing about the browser; the settings page and the autofill code sit on top of it.
///
/// What is protected, and from whom (the same words as in the About page, so it does not
/// promise more than it does):
///   - Someone who gets the file, the disk or a backup: user names and passwords are sealed
///     with AES-256-GCM under a random 256-bit data key, and that key is sealed too.
///   - In Dpapi mode the key is sealed by Windows for this user, so the file is of no use on
///     another account or machine.
///   - In Password mode the key is sealed with a key made from the NEAT password (PBKDF2) and
///     not by Windows at all, so the password is what protects it, and the file can be
///     moved to another machine.
///   - NOT protected: a program running as you on an unlocked machine. In Dpapi mode it can
///     ask Windows the same thing NEAT asks. In Password mode it can wait for you to unlock.
///   - The site (origin) of each entry is stored in the clear, because it is how entries are
///     found. It tells someone with the file which sites you have logins for.
///
/// Rules this code keeps:
///   - It never writes to a log, the console or the debugger, and never puts a user name or
///     password in an exception message. The self-check fails if a vault file mentions Log.
///   - A file it cannot read, cannot open or does not understand is never written over.
///   - Every write goes to a temporary file first and is then swapped in, so a crash in the
///     middle leaves the old file whole.
///   - The data key is replaced by a new random one whenever the way it is protected
///     changes (Switch), so an old copy of the file, with its old sealed key, cannot open
///     what is saved afterwards.
///
/// The operations that make a PBKDF2 key take a few tenths of a second: call them from a
/// background task, not from the UI thread. All methods are safe to call from several threads.
/// </summary>
public sealed class Vault : IDisposable
{
    /// <summary>The version of the file layout this code writes.</summary>
    public const int FormatVersion = 1;

    /// <summary>PBKDF2-SHA256 rounds for a new password key (OWASP's figure for SHA-256).</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>The shortest NEAT password accepted.</summary>
    public const int MinPassword = 8;

    private const string ModeDpapi = "dpapi";
    private const string ModePassword = "password";

    // Wrong passwords that cost nothing, then 2 s, 4 s, 8 s ... up to a minute. This slows down
    // someone at the keyboard. It cannot slow down someone with a copy of the file, who
    // tries passwords on their own machine: what stops them is the PBKDF2 cost.
    private const int FreeTries = 3;
    private const double MaxWaitSeconds = 60;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly IKeyWrap _wrap;
    private readonly int _iterations;

    // The clock the wrong-password wait and the self-check use.
    internal Func<DateTime> Clock = () => DateTime.UtcNow;

    private VaultFile? _file;
    private byte[]? _key;
    private int _fails;
    private DateTime _retryAt = DateTime.MinValue;

    public Vault(string path)
        : this(path, new Dpapi(), DefaultIterations)
    {
    }

    internal Vault(string path, IKeyWrap wrap, int iterations)
    {
        _path = path;
        _wrap = wrap;
        _iterations = iterations;
    }

    // ---- state ----

    /// <summary>True while the data key is in memory, that is, while entries can be read and changed.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _key is not null;
            }
        }
    }

    /// <summary>How the key is protected, or null when no vault file has been read.</summary>
    public VaultMode? Mode
    {
        get
        {
            lock (_gate)
            {
                if (_file is null)
                    return null;

                return _file.Mode == ModePassword ? VaultMode.Password : VaultMode.Dpapi;
            }
        }
    }

    /// <summary>How long until another password can be tried. Zero when there is no wait.</summary>
    public TimeSpan RetryAfter
    {
        get
        {
            lock (_gate)
            {
                var left = _retryAt - Clock();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    // ---- opening and locking ----

    /// <summary>
    /// Reads the vault. With no file it makes a new one in Dpapi mode. A Dpapi vault opens here
    /// by itself; a Password vault stays shut until <see cref="Unlock"/>. A file that cannot be
    /// used is reported and left exactly as it was. Writing the new file can throw
    /// (IOException, UnauthorizedAccessException) when the folder cannot be written.
    /// </summary>
    public OpenResult Open()
    {
        lock (_gate)
        {
            Lock();
            _file = null;
            _fails = 0;
            _retryAt = DateTime.MinValue;

            if (!File.Exists(_path))
                return Create();

            VaultFile? file;
            try
            {
                file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(_path), Json);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return OpenResult.Unreadable;
            }

            if (file is null || file.Version < 1 || file.Logins is null)
                return OpenResult.Unreadable;

            if (file.Version > FormatVersion)
                return OpenResult.TooNew;

            if (file.Mode == ModePassword)
            {
                var salt = FromBase64(file.Salt);
                if (salt is null || salt.Length < 16 || file.Iterations < 1 || file.Iterations > 100_000_000)
                    return OpenResult.Unreadable;

                _file = file;
                return OpenResult.NeedsPassword;
            }

            if (file.Mode != ModeDpapi)
                return OpenResult.Unreadable;

            _file = file;
            return OpenWithWindows() ? OpenResult.Ready : OpenResult.KeyLost;
        }
    }

    /// <summary>
    /// Opens a vault that was locked. In Dpapi mode the password is ignored. In Password mode
    /// it is checked by trying to open the key: there is no stored hash to compare with.
    /// </summary>
    public VaultResult Unlock(ReadOnlySpan<char> password = default)
    {
        lock (_gate)
        {
            if (_file is null)
                return VaultResult.Unavailable;

            if (_key is not null)
                return VaultResult.Ok;

            if (_file.Mode == ModeDpapi)
                return OpenWithWindows() ? VaultResult.Ok : VaultResult.KeyLost;

            return TryPassword(password, out _key);
        }
    }

    /// <summary>
    /// Whether this is the NEAT password, without opening or changing anything: for asking for
    /// the password again while the vault is already open. Counts wrong tries and makes the
    /// person wait exactly like <see cref="Unlock"/>.
    /// </summary>
    public VaultResult Check(ReadOnlySpan<char> password)
    {
        lock (_gate)
        {
            if (_file is null || _file.Mode != ModePassword)
                return VaultResult.Unavailable;

            var result = TryPassword(password, out var key);
            Seal.Wipe(key);
            return result;
        }
    }

    /// <summary>Forgets the data key. The entries stay on disk, sealed.</summary>
    public void Lock()
    {
        lock (_gate)
        {
            Seal.Wipe(_key);
            _key = null;
        }
    }

    public void Dispose()
    {
        Lock();
    }

    // ---- reading ----

    /// <summary>All entries, by site and then user name. Holds no passwords.</summary>
    public IReadOnlyList<LoginInfo> List()
    {
        lock (_gate)
        {
            var key = Key();

            return _file!.Logins
                .Select(r => Info(key, r))
                .OrderBy(i => i.Site, StringComparer.Ordinal)
                .ThenBy(i => i.User, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>The entries for exactly this origin (see <see cref="Origin"/>). Nothing for anything else.</summary>
    public IReadOnlyList<LoginInfo> Find(string site)
    {
        var origin = Origin.Of(site);
        if (origin is null)
            return Array.Empty<LoginInfo>();

        lock (_gate)
        {
            var key = Key();

            return _file!.Logins
                .Where(r => r.Site == origin)
                .Select(r => Info(key, r))
                .OrderBy(i => i.User, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// The password of an entry, only for someone who has just passed the gate. Null when there
    /// is no such entry or it does not check out. Dispose the result when done.
    /// </summary>
    public Secret? Reveal(Guid id, Access access)
    {
        if (access is null || !access.Valid)
            throw new UnauthorizedAccessException("The sign-in has expired.");

        lock (_gate)
        {
            var key = Key();
            var row = Find(id);
            if (row is null)
                return null;

            var plain = Open(key, row, "pass");
            return plain is null ? null : new Secret(plain);
        }
    }

    // ---- changing ----

    /// <summary>Saves a new login. The site must be an http or https address; only its origin is kept.</summary>
    public Guid Add(string site, string user, string password)
    {
        var origin = Origin.Of(site) ?? throw new ArgumentException("Not a web address.", nameof(site));

        lock (_gate)
        {
            var key = Key();
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            var row = new VaultRow
            {
                Id = id.ToString("N"),
                Site = origin,
                User = Sealed(key, id, origin, "user", user),
                Pass = Sealed(key, id, origin, "pass", password),
                Created = now,
                Changed = now,
            };

            _file!.Logins.Add(row);
            try
            {
                Write();
            }
            catch
            {
                _file.Logins.Remove(row);
                throw;
            }

            return id;
        }
    }

    /// <summary>
    /// Changes an entry. A null password keeps the old one. Everything is sealed again with new
    /// nonces. False when there is no such entry, or when the old password is needed and does
    /// not check out.
    /// </summary>
    public bool Update(Guid id, string site, string user, string? password)
    {
        var origin = Origin.Of(site) ?? throw new ArgumentException("Not a web address.", nameof(site));

        lock (_gate)
        {
            var key = Key();
            var old = Find(id);
            if (old is null)
                return false;

            var next = old.Copy();
            next.Site = origin;
            next.User = Sealed(key, id, origin, "user", user);
            next.Changed = DateTimeOffset.UtcNow;

            if (password is not null)
            {
                next.Pass = Sealed(key, id, origin, "pass", password);
            }
            else
            {
                var plain = Open(key, old, "pass");
                if (plain is null)
                    return false;

                try
                {
                    next.Pass = Sealed(key, id, origin, "pass", plain);
                }
                finally
                {
                    Seal.Wipe(plain);
                }
            }

            var index = _file!.Logins.IndexOf(old);
            _file.Logins[index] = next;
            try
            {
                Write();
            }
            catch
            {
                _file.Logins[index] = old;
                throw;
            }

            return true;
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            Key();

            var row = Find(id);
            if (row is null)
                return false;

            var index = _file!.Logins.IndexOf(row);
            _file.Logins.RemoveAt(index);
            try
            {
                Write();
            }
            catch
            {
                _file.Logins.Insert(index, row);
                throw;
            }

            return true;
        }
    }

    // ---- how the key is protected ----

    /// <summary>
    /// Protect the vault with a NEAT password instead of Windows (Dpapi to Password). The
    /// vault must be open. The Windows seal on the key is gone afterwards.
    /// </summary>
    public VaultResult UsePassword(ReadOnlySpan<char> password)
    {
        lock (_gate)
        {
            if (_file is null || _key is null)
                return VaultResult.Unavailable;

            if (password.Length < MinPassword)
                return VaultResult.TooShort;

            return Switch(VaultMode.Password, password);
        }
    }

    /// <summary>Protect the vault with Windows again instead of a password (Password to Dpapi). The vault must be open.</summary>
    public VaultResult UseDpapi()
    {
        lock (_gate)
        {
            if (_file is null || _key is null)
                return VaultResult.Unavailable;

            return Switch(VaultMode.Dpapi, default);
        }
    }

    /// <summary>
    /// A new NEAT password. The current one is checked first (with the same wait after wrong
    /// tries as Unlock). The data key changes too, so a copy of the file that was made
    /// under the old password does not open what is saved from now on.
    /// </summary>
    public VaultResult ChangePassword(ReadOnlySpan<char> current, ReadOnlySpan<char> next)
    {
        lock (_gate)
        {
            if (_file is null || _key is null || _file.Mode != ModePassword)
                return VaultResult.Unavailable;

            if (next.Length < MinPassword)
                return VaultResult.TooShort;

            var check = TryPassword(current, out var proved);
            if (check != VaultResult.Ok)
                return check;

            Seal.Wipe(proved);

            return Switch(VaultMode.Password, next);
        }
    }

    // ---- inside ----

    /// <summary>The data key, or an exception when the vault is not open.</summary>
    private byte[] Key()
    {
        return _key ?? throw new InvalidOperationException("The vault is locked.");
    }

    private VaultRow? Find(Guid id)
    {
        var text = id.ToString("N");
        return _file!.Logins.FirstOrDefault(r => r.Id == text);
    }

    private OpenResult Create()
    {
        var key = Seal.NewKey();

        _file = new VaultFile
        {
            Version = FormatVersion,
            Mode = ModeDpapi,
            Key = Convert.ToBase64String(_wrap.Protect(key)),
        };

        try
        {
            Write();
        }
        catch
        {
            _file = null;
            Seal.Wipe(key);
            throw;
        }

        _key = key;
        return OpenResult.New;
    }

    /// <summary>Opens the key with Windows. Lock is held.</summary>
    private bool OpenWithWindows()
    {
        var blob = FromBase64(_file!.Key);
        if (blob is null)
            return false;

        var key = _wrap.Unprotect(blob);
        if (key is null || key.Length != Seal.KeyBytes)
        {
            Seal.Wipe(key);
            return false;
        }

        _key = key;
        return true;
    }

    /// <summary>Tries a password against the sealed key. Lock is held.</summary>
    private VaultResult TryPassword(ReadOnlySpan<char> password, out byte[]? key)
    {
        key = null;

        var now = Clock();
        if (now < _retryAt)
            return VaultResult.Wait;

        var file = _file!;
        var salt = FromBase64(file.Salt);
        var blob = FromBase64(file.Key);
        if (salt is null || blob is null)
            return VaultResult.Unavailable;

        var derived = Derive(password, salt, file.Iterations);
        byte[]? opened;
        try
        {
            opened = Seal.Decrypt(derived, blob, Aad.Wrap(salt, file.Iterations));
        }
        finally
        {
            Seal.Wipe(derived);
        }

        if (opened is null || opened.Length != Seal.KeyBytes)
        {
            Seal.Wipe(opened);

            _fails++;
            if (_fails > FreeTries)
                _retryAt = now.AddSeconds(Math.Min(Math.Pow(2, _fails - FreeTries), MaxWaitSeconds));

            return VaultResult.Wrong;
        }

        _fails = 0;
        _retryAt = DateTime.MinValue;
        key = opened;
        return VaultResult.Ok;
    }

    /// <summary>
    /// Changes how the key is protected, and the key itself with it: every entry is opened with
    /// the old key and sealed again with a new random one. All of it is worked out first and
    /// only then written, in one swap; if that fails nothing has changed. Lock is held.
    /// </summary>
    private VaultResult Switch(VaultMode to, ReadOnlySpan<char> password)
    {
        var file = _file!;
        var old = _key!;
        var fresh = Seal.NewKey();

        var rows = new List<VaultRow>(file.Logins.Count);
        foreach (var row in file.Logins)
        {
            var again = Reseal(row, old, fresh);
            if (again is null)
            {
                Seal.Wipe(fresh);
                return VaultResult.Damaged;
            }

            rows.Add(again);
        }

        string mode;
        string wrapped;
        string? salt = null;
        var iterations = 0;

        if (to == VaultMode.Dpapi)
        {
            mode = ModeDpapi;
            wrapped = Convert.ToBase64String(_wrap.Protect(fresh));
        }
        else
        {
            var saltBytes = RandomNumberGenerator.GetBytes(16);
            var derived = Derive(password, saltBytes, _iterations);
            try
            {
                mode = ModePassword;
                iterations = _iterations;
                salt = Convert.ToBase64String(saltBytes);
                wrapped = Convert.ToBase64String(Seal.Encrypt(derived, fresh, Aad.Wrap(saltBytes, iterations)));
            }
            finally
            {
                Seal.Wipe(derived);
            }
        }

        var before = (file.Mode, file.Key, file.Salt, file.Iterations, file.Logins);

        file.Mode = mode;
        file.Key = wrapped;
        file.Salt = salt;
        file.Iterations = iterations;
        file.Logins = rows;

        try
        {
            Write();
        }
        catch
        {
            (file.Mode, file.Key, file.Salt, file.Iterations, file.Logins) = before;
            Seal.Wipe(fresh);
            throw;
        }

        Seal.Wipe(old);
        _key = fresh;
        return VaultResult.Ok;
    }

    /// <summary>One entry opened with <paramref name="old"/> and sealed again with <paramref name="fresh"/>. Null when it does not check out.</summary>
    private static VaultRow? Reseal(VaultRow row, byte[] old, byte[] fresh)
    {
        if (!Guid.TryParse(row.Id, out var id))
            return null;

        var user = Open(old, row, "user");
        var pass = Open(old, row, "pass");
        try
        {
            if (user is null || pass is null)
                return null;

            var again = row.Copy();
            again.User = Sealed(fresh, id, row.Site, "user", user);
            again.Pass = Sealed(fresh, id, row.Site, "pass", pass);
            return again;
        }
        finally
        {
            Seal.Wipe(user);
            Seal.Wipe(pass);
        }
    }

    private static LoginInfo Info(byte[] key, VaultRow row)
    {
        var user = Open(key, row, "user");
        var ok = user is not null && Guid.TryParse(row.Id, out _);

        try
        {
            return new LoginInfo(
                Guid.TryParse(row.Id, out var id) ? id : Guid.Empty,
                row.Site,
                user is null ? string.Empty : Encoding.UTF8.GetString(user),
                row.Created,
                row.Changed,
                !ok);
        }
        finally
        {
            Seal.Wipe(user);
        }
    }

    // ---- sealing fields ----

    private static string Sealed(byte[] key, Guid id, string site, string field, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            return Sealed(key, id, site, field, bytes);
        }
        finally
        {
            Seal.Wipe(bytes);
        }
    }

    private static string Sealed(byte[] key, Guid id, string site, string field, byte[] bytes)
    {
        return Convert.ToBase64String(Seal.Encrypt(key, bytes, Aad.Field(id, site, field)));
    }

    /// <summary>One field of an entry opened, or null when it does not check out. The caller wipes the bytes.</summary>
    private static byte[]? Open(byte[] key, VaultRow row, string field)
    {
        if (!Guid.TryParse(row.Id, out var id))
            return null;

        var blob = FromBase64(field == "user" ? row.User : row.Pass);
        return blob is null ? null : Seal.Decrypt(key, blob, Aad.Field(id, row.Site, field));
    }

    // ---- password key ----

    private static byte[] Derive(ReadOnlySpan<char> password, byte[] salt, int iterations)
    {
        // A letter with an accent can be typed as one character or as the letter plus a
        // separate accent mark. Bring both to one form, so the password works however the
        // keyboard produced it (this matters for Vietnamese). It is the same form every time.
        var text = password.ToString().Normalize(NormalizationForm.FormC);

        return Rfc2898DeriveBytes.Pbkdf2(text, salt, iterations, HashAlgorithmName.SHA256, Seal.KeyBytes);
    }

    // ---- the file ----

    /// <summary>
    /// Writes the vault beside the real file and then swaps it in, so a crash in the middle
    /// leaves the old one whole. A later version that changes the layout copies the old file
    /// to vault.v&lt;n&gt;.bak first, before it rewrites anything.
    /// </summary>
    private void Write()
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(folder);

        var temp = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_file, Json);

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);

            // Out of the disk cache, so the swap below never points at a file that is still in memory.
            stream.Flush(true);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static byte[]? FromBase64(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
