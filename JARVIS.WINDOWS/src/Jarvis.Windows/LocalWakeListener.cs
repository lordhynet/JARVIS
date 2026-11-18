using global::Windows.ApplicationModel;
using global::Windows.Media.SpeechRecognition;
using Jarvis.Windows.Client;

namespace Jarvis.Windows;

internal static class PackageIdentity
{
    public static void Require()
    {
        try { _ = Package.Current.Id.Name; }
        catch { throw new InvalidOperationException("Install the reviewed MSIX package to enable Windows speech and microphone access."); }
    }
}

internal sealed class LocalWakeListener : IWakeListener
{
    private readonly SemaphoreSlim lifecycle = new(1);
    private SpeechRecognizer? recognizer;
    private volatile bool listening, wanted;
    public event Action<string, bool>? Detected;
    public event Action<string>? Unavailable;
    public async Task StartAsync(CancellationToken ct)
    {
        wanted = true;
        await StartCoreAsync(ct);
    }
    private async Task StartCoreAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        using var start = CancellationTokenSource.CreateLinkedTokenSource(ct); start.CancelAfter(TimeSpan.FromSeconds(10));
        try {
            if (!wanted || listening) return;
            PackageIdentity.Require();
            if (recognizer is null) {
                var language = SpeechRecognizer.SupportedGrammarLanguages.OrderBy(l => l.LanguageTag == "en-GB" ? 0 : 1)
                    .FirstOrDefault(l => l.LanguageTag.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Install an English on-device Windows speech language first.");
                recognizer = new SpeechRecognizer(language);
                recognizer.Constraints.Add(new SpeechRecognitionListConstraint([WakeMatcher.Phrase], "jarvis-wake"));
                var compilation = await recognizer.CompileConstraintsAsync().AsTask(start.Token);
                if (compilation.Status != SpeechRecognitionResultStatus.Success) throw new InvalidOperationException("Local speech grammar is unavailable.");
                recognizer.ContinuousRecognitionSession.ResultGenerated += (_, result) => {
                    if (listening && result.Result.Status == SpeechRecognitionResultStatus.Success)
                        Detected?.Invoke(result.Result.Text, result.Result.Confidence == SpeechRecognitionConfidence.High);
                };
                recognizer.ContinuousRecognitionSession.Completed += (sender, result) => {
                    if (!listening) return;
                    listening = false;
                    if (result.Status is SpeechRecognitionResultStatus.Success or SpeechRecognitionResultStatus.TimeoutExceeded) _ = Task.Run(RestartAsync);
                    else { wanted = false; Unavailable?.Invoke("Local speech recognition stopped."); }
                };
            }
            start.Token.ThrowIfCancellationRequested();
            listening = true;
            try { await recognizer.ContinuousRecognitionSession.StartAsync().AsTask(start.Token); }
            catch { listening = false; throw; }
        } catch { listening = false; recognizer?.Dispose(); recognizer = null; throw; }
        finally { lifecycle.Release(); }
    }
    private async Task RestartAsync()
    {
        try { await StartCoreAsync(CancellationToken.None); }
        catch { wanted = false; Unavailable?.Invoke("Local speech recognition stopped."); }
    }
    public async Task StopAsync()
    {
        wanted = false;
        await lifecycle.WaitAsync();
        try {
            if (!listening) return;
            listening = false;
            if (recognizer is not null) try { await recognizer.ContinuousRecognitionSession.CancelAsync(); } catch { }
        } finally { lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); recognizer?.Dispose(); }
}
