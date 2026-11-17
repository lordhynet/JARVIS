namespace Jarvis.Orchestrator.Runtime;

public readonly record struct VoiceWindowLimits
{
    public const int DefaultSeconds = 300;
    public const int SampleRate = 24000;
    public const int BytesPerSecond = SampleRate * 2;

    public VoiceWindowLimits(int seconds)
    {
        Guard.Require(seconds is >= 30 and <= 600, 503, "The realtime voice window must be between 30 and 600 seconds.");
        Seconds = seconds;
    }

    public int Seconds { get; }
    public int MaxTurns => (Seconds * 8 + 59) / 60;
    public int MaxInputBytes => BytesPerSecond * Seconds;
}
