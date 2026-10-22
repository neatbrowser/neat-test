using Windows.Security.Credentials.UI;

namespace neat;

/// <summary>
/// Windows Hello (fingerprint, face or PIN) as the gate in front of the saved passwords. It
/// answers the questions of <see cref="IHello"/>; the rules that use the answers are in
/// vault/gate.cs, which can be checked without a Windows PC.
///
/// Anything that goes wrong is written to crash.log and answered as "no": a Hello that fails
/// to start must never let anyone in. (No password or user name passes through here.)
/// </summary>
internal sealed class Hello : IHello
{
    // The window the prompt belongs to, asked for when the prompt is needed.
    private readonly Func<IntPtr> _window;

    public Hello(Func<IntPtr> window)
    {
        _window = window;
    }

    public async Task<HelloState> StateAsync()
    {
        try
        {
            var found = await UserConsentVerifier.CheckAvailabilityAsync();

            return found switch
            {
                UserConsentVerifierAvailability.Available => HelloState.Ready,
                UserConsentVerifierAvailability.NotConfiguredForUser => HelloState.NotSetUp,
                UserConsentVerifierAvailability.DisabledByPolicy => HelloState.Blocked,
                UserConsentVerifierAvailability.DeviceNotPresent => HelloState.NoDevice,
                _ => HelloState.Unknown,
            };
        }
        catch (Exception ex)
        {
            Log.Error("hello", ex, "availability check failed");
            return HelloState.Unknown;
        }
    }

    public async Task<bool> AskAsync(string message)
    {
        try
        {
            var result = await Request(message);
            return result == UserConsentVerificationResult.Verified;
        }
        catch (Exception ex)
        {
            Log.Error("hello", ex, "verification failed");
            return false;
        }
    }

    /// <summary>
    /// Shows the Hello prompt. A desktop app has to say which window the prompt belongs to,
    /// and the Windows helper for that (UserConsentVerifierInterop) is looked up by name: if
    /// this build of the Windows SDK projection does not have it, the plain call is used
    /// instead, which also works for many desktop apps. If neither works the caller gets an
    /// exception, which AskAsync turns into "no".
    /// </summary>
    private async Task<UserConsentVerificationResult> Request(string message)
    {
        var helper = typeof(UserConsentVerifier).Assembly
            .GetType("Windows.Security.Credentials.UI.UserConsentVerifierInterop");

        var method = helper?.GetMethod("RequestVerificationForWindowAsync", new[] { typeof(IntPtr), typeof(string) });
        if (method is not null)
        {
            var started = method.Invoke(null, new object[] { _window(), message });
            if (started is Windows.Foundation.IAsyncOperation<UserConsentVerificationResult> operation)
                return await operation;
        }

        return await UserConsentVerifier.RequestVerificationAsync(message);
    }
}
