using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Forge;

namespace Jarvis.Orchestrator.Cqrs.Forge;

public sealed record InstallForgeDivisionDrafts(InstallForgeDivisionsRequest Request) : ICommand<IReadOnlyList<ForgeDivisionDraft>>;

/// <summary>
/// PF-02. Saves each Forge division through the existing registry draft path with a deterministic request ID,
/// so repeats and concurrent installs replay instead of duplicating. Existing definitions (including ones the owner
/// edited, disabled or activated) are never overwritten. Activation stays the owner's existing approval step.
/// </summary>
public sealed class ForgeDivisionCommands(IDomainRegistry registry, IForgeAvailability feature) :
    ICommandHandler<InstallForgeDivisionDrafts, IReadOnlyList<ForgeDivisionDraft>>
{
    public async Task<IReadOnlyList<ForgeDivisionDraft>> HandleAsync(InstallForgeDivisionDrafts command, CancellationToken ct = default)
    {
        ForgeProgrammePolicy.RequireEnabled(feature);
        var existing = (await registry.ListAsync(ct)).Select(p => p.Definition.Key).ToHashSet(StringComparer.Ordinal);
        var results = new List<ForgeDivisionDraft>(ForgeDivisions.All.Count);
        foreach (var division in ForgeDivisions.All) {
            ct.ThrowIfCancellationRequested();
            if (division.Optional && !command.Request.IncludeOptional) {
                results.Add(new(division.Key, division.Code, "skipped-optional", division.Protected, division.Optional, null)); continue;
            }
            if (existing.Contains(division.Key)) {
                results.Add(new(division.Key, division.Code, "existing", division.Protected, division.Optional, null)); continue;
            }
            var manifest = JsonSerializer.SerializeToElement(ForgeDivisions.Manifest(division), DomainPolicy.Json);
            var preview = await registry.SaveDraftAsync(new(DomainPolicy.StableId("forge-division-draft:v1:" + division.Key), null, manifest),
                ForgeDivisions.DraftActor, ct);
            results.Add(new(division.Key, division.Code, "drafted", division.Protected, division.Optional, preview.Risk));
        }
        return results.AsReadOnly();
    }
}
