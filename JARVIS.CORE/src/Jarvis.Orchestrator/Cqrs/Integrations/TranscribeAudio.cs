using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Integrations;

// This is an immediate command, not a durable message: the caller owns the stream.
// It invokes a metered external operation even though it does not change local state.
public sealed record TranscribeAudio(Stream Audio, string Filename, long Length) : ICommand<string>;

public sealed class TranscribeAudioHandler(ITranscriptionProvider provider) : ICommandHandler<TranscribeAudio, string>
{
    public Task<string> HandleAsync(TranscribeAudio command, CancellationToken ct = default)
    {
        Guard.Require(command.Length is > 0 and <= 10_000_000, 400, "Audio must be between 1 byte and 10 MB.");
        var extension = Path.GetExtension(command.Filename).ToLowerInvariant();
        Guard.Require(new[] { ".webm", ".wav", ".mp3", ".m4a", ".mp4", ".mpeg", ".mpga" }.Contains(extension), 400, "Unsupported audio format.");
        return provider.TranscribeAsync(command.Audio, "recording" + extension, ct);
    }
}
