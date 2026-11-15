namespace Jarvis.Tests;

public sealed class ProviderDispatchArchitectureTests
{
    [Fact]
    public void Known_provider_dispatch_adapters_have_a_declared_reservation_before_their_send_chain()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jarvis.sln"))) directory = directory.Parent;
        Assert.NotNull(directory); var root = Path.Combine(directory.FullName, "src", "Jarvis.Api");
        var dispatcher = File.ReadAllText(Path.Combine(root, "Integrations", "TextReasoningService.cs"));
        Assert.True(dispatcher.IndexOf("budget.ReserveAsync(", StringComparison.Ordinal) < dispatcher.IndexOf("adapter.ExecuteAsync(", StringComparison.Ordinal));
        var known = new HashSet<string>(StringComparer.Ordinal) { "ManagedAi.cs", "OpenAiProvider.cs", "TextReasoningAdapters.cs", "RealtimeGateway.cs" };
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))) {
            var text = File.ReadAllText(file);
            if (!(text.Contains("api.openai.com", StringComparison.Ordinal) || text.Contains("api.anthropic.com", StringComparison.Ordinal) || text.Contains("generativelanguage.googleapis.com", StringComparison.Ordinal))) continue;
            Assert.Contains(Path.GetFileName(file), known);
            var reserve = text.IndexOf(Path.GetFileName(file) == "TextReasoningAdapters.cs" ? "ManagedAi.RequireReservedAsync(" : "budget.ReserveAsync(", StringComparison.Ordinal);
            Assert.True(reserve >= 0, "Provider adapter must reserve or verify its durable reservation: " + Path.GetFileName(file));
            var send = Path.GetFileName(file) switch {
                "ManagedAi.cs" => text.IndexOf("CoreObservability.SendAsync(", StringComparison.Ordinal),
                "OpenAiProvider.cs" => text.IndexOf("gateway.JsonAsync(request", StringComparison.Ordinal),
                "TextReasoningAdapters.cs" => text.IndexOf("CoreObservability.SendAsync(", reserve, StringComparison.Ordinal),
                _ => text.IndexOf("connections.ConnectAsync(", StringComparison.Ordinal)
            };
            Assert.True(send > reserve, "Provider send must follow reservation: " + Path.GetFileName(file));
        }
    }
}
