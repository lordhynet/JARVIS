using Jarvis.Api;
using Jarvis.Contracts;

namespace Jarvis.Tests;

public sealed class IdentifierContractTests
{
    [Fact]
    public void Http_headers_and_configuration_keys_keep_their_public_names()
    {
        Assert.Equal("X-Jarvis-Key", CoreHeaders.ApiKey);
        Assert.Equal("X-Jarvis-Owner-Key", CoreHeaders.OwnerKey);
        Assert.Equal("X-Jarvis-Data-Epoch", CoreHeaders.DataEpoch);
        Assert.Equal("Jarvis:ApiKey", ConfigKeys.CoreApiKey);
        Assert.Equal("BudgetManagement:OwnerKey", ConfigKeys.OwnerKey);
        Assert.Equal("BudgetManagement:SubjectId", ConfigKeys.OwnerSubjectId);
        Assert.Equal("OpenAI:ApiKey", ConfigKeys.OpenAiApiKey);
        Assert.Equal("Anthropic:ApiKey", ConfigKeys.AnthropicApiKey);
        Assert.Equal("AI:Research:Model", ConfigKeys.AiProfilePrefix("Research") + ConfigKeys.Model);
        Assert.Equal("AI:Voice:RateCardDate", ConfigKeys.VoiceProfile + ConfigKeys.RateCardDate);
        Assert.Equal("AI:Transcription:RateCardModel", ConfigKeys.TranscriptionProfile + ConfigKeys.RateCardModel);
        Assert.Equal("PaidAI:Enabled", ConfigKeys.PaidAiEnabled);
    }

    [Fact]
    public void Provider_ids_and_chat_roles_keep_their_persisted_values()
    {
        Assert.Equal("openai", ProviderIds.OpenAi);
        Assert.Equal("anthropic", ProviderIds.Anthropic);
        Assert.Equal("user", ChatRoles.User);
        Assert.Equal("assistant", ChatRoles.Assistant);
        Assert.Equal("user", TranscriptSegment.UserSpeaker);
    }

    [Fact]
    public void Agent_run_states_keep_their_persisted_values()
    {
        Assert.Equal("Queued", AgentRunStatus.Queued);
        Assert.Equal("Running", AgentRunStatus.Running);
        Assert.Equal("Succeeded", AgentRunStatus.Succeeded);
        Assert.Equal("Failed", AgentRunStatus.Failed);
        Assert.Equal("Cancelled", AgentRunStatus.Cancelled);
        Assert.Equal("Expired", AgentRunStatus.Expired);
        Assert.Equal("NeedsReconciliation", AgentRunStatus.NeedsReconciliation);
        Assert.Equal("SourceDeleted", AgentRunStatus.SourceDeleted);
    }

    [Fact]
    public void Goal_states_keep_their_persisted_values()
    {
        Assert.Equal("Active", GoalStatus.Active);
        Assert.Equal("Paused", GoalStatus.Paused);
        Assert.Equal("Completed", GoalStatus.Completed);
        Assert.Equal("Cancelled", GoalStatus.Cancelled);
        Assert.Equal("Deleted", GoalStatus.Deleted);
        Assert.Equal("Expired", GoalStatus.Expired);
    }

    [Fact]
    public void Agent_trigger_states_keep_their_persisted_values()
    {
        Assert.Equal("Pending", AgentTriggerStatus.Pending);
        Assert.Equal("Dispatched", AgentTriggerStatus.Dispatched);
        Assert.Equal("Cancelled", AgentTriggerStatus.Cancelled);
        Assert.Equal("Expired", AgentTriggerStatus.Expired);
    }

    [Fact]
    public void Provider_attempt_outcomes_keep_their_persisted_values()
    {
        Assert.Equal("Preparing", ProviderAttemptOutcome.Preparing);
        Assert.Equal("Cached", ProviderAttemptOutcome.Cached);
        Assert.Equal("Completed", ProviderAttemptOutcome.Completed);
        Assert.Equal("Failed", ProviderAttemptOutcome.Failed);
        Assert.Equal("Refused", ProviderAttemptOutcome.Refused);
        Assert.Equal("Uncertain", ProviderAttemptOutcome.Uncertain);
    }
}
