namespace neat;

/// <summary>Whether Windows Hello (fingerprint, face or PIN) can be used on this PC.</summary>
public enum HelloState
{
    /// <summary>Set up and ready.</summary>
    Ready,

    /// <summary>The PC can do it but this Windows user has not set it up.</summary>
    NotSetUp,

    /// <summary>Turned off by a policy.</summary>
    Blocked,

    /// <summary>No fingerprint reader, camera or PIN support.</summary>
    NoDevice,

    /// <summary>Busy, or the check itself failed. Treated as not usable.</summary>
    Unknown,
}

/// <summary>
/// Windows Hello as the gate. It is an interface so the rules of the gate can be checked
/// without a Windows PC; the real one is in pass/hello.cs.
/// </summary>
public interface IHello
{
    Task<HelloState> StateAsync();

    /// <summary>
    /// True only when the person has just proved who they are. Anything that goes wrong must
    /// come back as false: an error is never a yes.
    /// </summary>
    Task<bool> AskAsync(string message);
}

/// <summary>What stands between a passer-by and the saved passwords right now.</summary>
public enum Protection
{
    /// <summary>Windows Hello is asked before a password is shown or copied.</summary>
    Hello,

    /// <summary>The vault is sealed with the NEAT password and opens only with it.</summary>
    Password,

    /// <summary>
    /// Windows seals the key and Hello cannot be used, so nothing is asked. Passwords are not
    /// shown or copied until a NEAT password is set.
    /// </summary>
    None,
}

/// <summary>
/// The gate in front of showing, copying and deleting passwords: decides how the person proves
/// who they are and hands out the <see cref="Access"/> that <see cref="Vault.Reveal"/> wants.
/// It has no screen; the settings page asks it and shows the answers.
///
///   - Windows protection with Hello ready: Hello is asked.
///   - Password protection: the vault is shut until the password is typed. The password
///     is the proof, and the vault is shut again when the access runs out.
///   - Windows protection without Hello: no access is given at all.
///
/// An access lasts <see cref="Idle"/> after the last thing the person did with it (Touch).
/// The page calls <see cref="Tick"/> every few seconds so that an unused access is ended and,
/// for a password vault, the key is forgotten. Any failure when asking counts as no.
/// </summary>
public sealed class Gate
{
    /// <summary>How long an access lasts without use.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);

    private readonly Vault _vault;
    private readonly IHello _hello;

    // The clock the access (and the self-check) use.
    internal Func<DateTime> Clock = () => DateTime.UtcNow;

    private Access? _access;

    public Gate(Vault vault, IHello hello)
    {
        _vault = vault;
        _hello = hello;
    }

    /// <summary>What Windows Hello said last time <see cref="ProtectionAsync"/> asked.</summary>
    public HelloState Hello { get; private set; } = HelloState.Unknown;

    /// <summary>The access the person has now, or null when there is none or it ran out.</summary>
    public Access? Current => _access is { Valid: true } ? _access : null;

    /// <summary>Asks Windows Hello again and works out what protects the vault.</summary>
    public async Task<Protection> ProtectionAsync()
    {
        try
        {
            Hello = await _hello.StateAsync();
        }
        catch (Exception)
        {
            Hello = HelloState.Unknown;
        }

        return _vault.Mode switch
        {
            VaultMode.Password => Protection.Password,
            VaultMode.Dpapi when Hello == HelloState.Ready => Protection.Hello,
            _ => Protection.None,
        };
    }

    /// <summary>
    /// Asks Windows Hello and, on a yes, starts an access. Only for a vault under Windows
    /// protection that is open; false in every other case, and for anything that goes wrong.
    /// </summary>
    public async Task<bool> SignInAsync(string message)
    {
        if (_vault.Mode != VaultMode.Dpapi || !_vault.IsOpen)
            return false;

        bool yes;
        try
        {
            yes = await _hello.AskAsync(message);
        }
        catch (Exception)
        {
            yes = false;
        }

        if (!yes)
            return false;

        _access = new Access(Idle, Clock);
        return true;
    }

    /// <summary>
    /// Opens a password vault with the password and starts an access. It is slow (the password
    /// key takes a few tenths of a second): call it from a background task.
    /// </summary>
    public VaultResult SignIn(ReadOnlySpan<char> password)
    {
        if (_vault.Mode != VaultMode.Password)
            return VaultResult.Unavailable;

        var result = _vault.IsOpen ? _vault.Check(password) : _vault.Unlock(password);
        if (result == VaultResult.Ok)
            _access = new Access(Idle, Clock);

        return result;
    }

    /// <summary>
    /// Starts an access for someone who has just proved it another way: typed a new password
    /// twice, for one. Do not call it for anything the person has not just done.
    /// </summary>
    public void Grant()
    {
        _access = new Access(Idle, Clock);
    }

    /// <summary>The person is still at it: the access runs out <see cref="Idle"/> after now.</summary>
    public void Touch()
    {
        _access?.Touch(Idle);
    }

    /// <summary>Ends the access. A password vault is shut as well.</summary>
    public void SignOut()
    {
        _access?.Revoke();
        _access = null;

        if (_vault.Mode == VaultMode.Password)
            _vault.Lock();
    }

    /// <summary>
    /// For a timer. Ends an access that has run out (shutting a password vault) and says
    /// true on the one call that does so, so the page can redraw.
    /// </summary>
    public bool Tick()
    {
        if (_access is null || _access.Valid)
            return false;

        SignOut();
        return true;
    }
}
