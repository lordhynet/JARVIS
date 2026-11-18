using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Jarvis.Windows.Client;

public sealed record Connection(Uri Origin, string Credential, string? CertificateSha256 = null, string? DeviceId = null,
    bool AllowCloudProcessing = false, long? PrivacyEpoch = null, bool ListenOnStartup = false)
{
    public override string ToString() => Origin.GetLeftPart(UriPartial.Authority);

    public void Validate()
    {
        if (Origin.Scheme != "https" || Origin.UserInfo.Length != 0 || Origin.Query.Length != 0 || Origin.Fragment.Length != 0 ||
            Origin.AbsolutePath != "/" || Origin.HostNameType != UriHostNameType.Dns && !Origin.IsLoopback)
            throw new InvalidOperationException("Use a trusted HTTPS DNS address, or loopback HTTPS with a certificate pin.");
        if (Credential.Length != 69 || !Credential.StartsWith("jdv1_", StringComparison.Ordinal) || !Credential[5..].All(Uri.IsHexDigit))
            throw new InvalidOperationException("Use this Windows device's scoped jdv1_ credential from Core.");
        if (Origin.IsLoopback && CertificateSha256 is null)
            throw new InvalidOperationException("Loopback HTTPS requires its certificate SHA-256 pin.");
        if (CertificateSha256 is not null && (!Origin.IsLoopback || CertificateSha256.Length != 64 || !CertificateSha256.All(Uri.IsHexDigit)))
            throw new InvalidOperationException("A certificate pin must be 64 hexadecimal characters and is supported for loopback only.");
    }

    public bool AcceptCertificate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        if (CertificateSha256 is null) return errors == SslPolicyErrors.None;
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
        using var leaf = new X509Certificate2(certificate);
        var now = DateTime.UtcNow;
        return leaf.NotBefore.ToUniversalTime() <= now && leaf.NotAfter.ToUniversalTime() > now &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(leaf.RawData), Convert.FromHexString(CertificateSha256));
    }
}

public static class WakeMatcher
{
    public const string Phrase = "Hey JARVIS";
    public static bool Accept(string text, bool highConfidence) => highConfidence &&
        string.Equals(text.Trim(), Phrase, StringComparison.OrdinalIgnoreCase);
}

public readonly record struct PlaybackPosition(string ItemId, long Milliseconds);

public interface ICompanionAudio : IAsyncDisposable
{
    event Action<byte[]>? Captured;
    event Action? Failed;
    Task StartAsync(CancellationToken ct);
    void Play(string itemId, ReadOnlySpan<byte> pcm);
    PlaybackPosition? Interrupt();
    void Stop();
    Task SpeakAsync(string text, CancellationToken ct);
}

public interface IWakeListener : IAsyncDisposable
{
    event Action<string, bool>? Detected;
    event Action<string>? Unavailable;
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}

public interface ILockState { bool IsLocked { get; } }
