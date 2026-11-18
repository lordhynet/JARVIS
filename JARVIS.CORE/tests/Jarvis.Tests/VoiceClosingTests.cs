using Jarvis.Api.Voice;

namespace Jarvis.Tests;

public sealed class VoiceClosingTests
{
    [Theory]
    [InlineData("Thank you, JARVIS.")]
    [InlineData("thanks jarvis")]
    [InlineData("Thanks JARVIS, that's all from me, bye!")]
    [InlineData("That’s all from me")]
    [InlineData("thats all")]
    [InlineData("Bye.")]
    [InlineData("Later, JARVIS")]
    [InlineData("Okay, bye for now")]
    [InlineData("Perfect, thank you very much")]
    [InlineData("No, that's it for now. Goodbye.")]
    [InlineData("Talk to you later")]
    [InlineData("Stop listening")]
    [InlineData("Cheers mate")]
    [InlineData("Good night, JARVIS")]
    public void Whole_utterances_of_closing_words_end_the_conversation(string transcript) =>
        Assert.True(VoiceClosing.IsClosing(transcript));

    [Theory]
    [InlineData("Thank you, and what about Thursday?")]
    [InlineData("Thanks, can you ask me another question?")]
    [InlineData("How do I stop a process?")]
    [InlineData("Remind me later")]
    [InlineData("Yes thanks")]
    [InlineData("That's all wrong")]
    [InlineData("JARVIS")]
    [InlineData("Okay")]
    [InlineData("stop")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Requests_and_partial_phrases_never_end_the_conversation(string? transcript) =>
        Assert.False(VoiceClosing.IsClosing(transcript));

    [Fact]
    public void Long_utterances_are_never_treated_as_goodbye()
    {
        Assert.False(VoiceClosing.IsClosing(string.Join(' ', Enumerable.Repeat("thanks", 30))));
        Assert.False(VoiceClosing.IsClosing(new string('b', 201)));
    }

    [Fact]
    public void Idle_rule_waits_for_silence_and_no_pending_reply()
    {
        Assert.False(VoiceClosing.IsIdle(TimeSpan.FromSeconds(59), responsePending: false, closing: false));
        Assert.True(VoiceClosing.IsIdle(TimeSpan.FromSeconds(60), responsePending: false, closing: false));
        Assert.False(VoiceClosing.IsIdle(TimeSpan.FromMinutes(5), responsePending: true, closing: false));
        Assert.False(VoiceClosing.IsIdle(TimeSpan.FromMinutes(5), responsePending: false, closing: true));
        Assert.False(VoiceClosing.IsIdle(TimeSpan.FromSeconds(-10), responsePending: false, closing: false));
    }

    [Fact]
    public void Grace_covers_the_farewell_audio_within_a_bound()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), VoiceClosing.GraceAfterFarewell(0));
        Assert.Equal(TimeSpan.FromSeconds(5), VoiceClosing.GraceAfterFarewell(2));
        Assert.Equal(TimeSpan.FromSeconds(15), VoiceClosing.GraceAfterFarewell(60));
    }
}
