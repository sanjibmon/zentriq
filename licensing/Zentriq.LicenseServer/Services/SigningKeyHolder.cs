namespace Zentriq.LicenseServer.Services;

/// <summary>
/// The signing key in use. Configured servers load it at start-up (a missing or wrong key stops them). An installed
/// server can run without one until the setup wizard has created or imported it; until then licensing answers
/// "not set up" (503).
/// </summary>
public sealed class SigningKeyHolder
{
    private volatile SigningKey? _key;
    public SigningKeyHolder(SigningKey? key = null) => _key = key;

    public SigningKey? Current => _key;
    /// <summary>Why the key could not be loaded at start-up (shown by the setup page).</summary>
    public string? LoadError { get; set; }

    public SigningKey Require() => _key ?? throw new NotSetUpException();

    public void Set(SigningKey key) { _key = key; LoadError = null; }
}

public sealed class NotSetUpException() : InvalidOperationException(Message)
{
    public new const string Message = "The license server is not set up yet: finish the setup at http://localhost:5080/setup on the license server.";
}
