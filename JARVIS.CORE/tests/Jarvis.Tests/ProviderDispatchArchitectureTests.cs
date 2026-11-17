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
            var adapter = Path.GetFileName(file);
            Assert.Contains(adapter, known);
            if (adapter == "RealtimeGateway.cs")
            {
                var session = File.ReadAllText(Path.Combine(root, "Voice", "RealtimeVoiceSession.cs"));
                Assert.Contains("await budget.ReserveAsync(", session, StringComparison.Ordinal);
                AssertReservationBeforeConnect("await PrepareWindowAsync(timeout.Token)", "await ConnectReservedAsync(timeout.Token)");
                AssertReservationBeforeConnect("await PrepareWindowAsync(ct)", "await ConnectReservedAsync(ct)");
                AssertReservationBeforeConnect("Guard.Require(reservation is not null", "connections.ConnectAsync(");
                Assert.Equal(session.IndexOf("connections.ConnectAsync(", StringComparison.Ordinal),
                    session.LastIndexOf("connections.ConnectAsync(", StringComparison.Ordinal));
                Assert.Contains("await current.RunAsync(", text, StringComparison.Ordinal);
                continue;

                void AssertReservationBeforeConnect(string admission, string connect)
                {
                    var prepare = session.IndexOf(admission, StringComparison.Ordinal);
                    Assert.True(prepare >= 0 && session.IndexOf(connect, StringComparison.Ordinal) > prepare,
                        "The voice connection must follow reservation: " + connect);
                }
            }
            var reserve = text.IndexOf(adapter == "TextReasoningAdapters.cs" ? "ManagedAi.RequireReservedAsync(" : "budget.ReserveAsync(", StringComparison.Ordinal);
            Assert.True(reserve >= 0, "Provider adapter must reserve or verify its durable reservation: " + Path.GetFileName(file));
            var send = adapter switch {
                "ManagedAi.cs" => text.IndexOf("CoreObservability.SendAsync(", StringComparison.Ordinal),
                "OpenAiProvider.cs" => text.IndexOf("gateway.JsonAsync(request", StringComparison.Ordinal),
                _ => text.IndexOf("CoreObservability.SendAsync(", reserve, StringComparison.Ordinal)
            };
            Assert.True(send > reserve, "Provider send must follow reservation: " + Path.GetFileName(file));
        }
    }
}
