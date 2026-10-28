using System.Collections.Immutable;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Core.Environments;

public sealed record DependencyChoice(Guid DependentId, DependencyRequirement Requirement, ImmutableArray<Guid> Candidates);

public sealed class EnvironmentDraft
{
    private string originalHash;
    private EnvironmentDraft(IsolatedEnvironment snapshot, bool persisted)
    { Snapshot = snapshot; IsPersisted = persisted; originalHash = CanonicalJson.Hash(snapshot); }
    public Guid DraftId { get; } = Guid.NewGuid();
    public IsolatedEnvironment Snapshot { get; private set; }
    public bool IsPersisted { get; private set; }
    public bool IsDirty => originalHash != CanonicalJson.Hash(Snapshot);
    public static EnvironmentDraft Create() => new(new IsolatedEnvironment("environment", 1, Guid.NewGuid(), 1, "", [], [], new DataBaseline([])), false);
    public static EnvironmentDraft Open(IsolatedEnvironment snapshot) => new(snapshot, true);
    public void Update(IsolatedEnvironment snapshot)
    {
        if (snapshot.Id != Snapshot.Id || snapshot.Revision != Snapshot.Revision) { throw new InvalidOperationException("Draft identity is immutable."); }
        Snapshot = snapshot;
    }
    public void MarkSaved() { originalHash = CanonicalJson.Hash(Snapshot); IsPersisted = true; }
    public void AcceptSaved(IsolatedEnvironment snapshot)
    {
        if (snapshot.Id != Snapshot.Id || snapshot.Revision < Snapshot.Revision || snapshot.Revision > Snapshot.Revision + 1) { throw new InvalidOperationException("Saved draft identity mismatch."); }
        Snapshot = snapshot; MarkSaved();
    }

    public void AddComponent(ComponentDefinition definition, IReadOnlyList<ComponentDefinition> library)
    {
        IsolatedEnvironment before = Snapshot;
        var expanding = new HashSet<(Guid, string)>();
        try { Add(definition); BindUnambiguous(); }
        catch { Snapshot = before; throw; }
        void Add(ComponentDefinition item)
        {
            EnvironmentValidator.ValidateDefinition(item);
            if (!expanding.Add((item.Id, item.Version))) { return; }
            var component = new EnvironmentComponent(Guid.NewGuid(), item, item.DefaultSource,
                item.ParameterDefinitions.Where(p => p.DefaultValue is not null).ToImmutableDictionary(p => p.Name, p => p.DefaultValue!, StringComparer.Ordinal),
                item.DefaultEnvironment, item.DefaultPorts, item.DefaultStorage);
            Snapshot = Snapshot with { Components = Snapshot.Components.Add(component) };
            if (item.Initialization is not null)
            { Snapshot = Snapshot with { Baseline = new DataBaseline(Snapshot.Baseline.Applications.Add(new BaselineApplication(component.Id, item.Initialization, null))) }; }
            foreach (DependencyRequirement requirement in item.DependencyRequirements.Where(requirement => !requirement.Optional))
            {
                if (Snapshot.Components.Any(c => Matches(c, requirement))) { continue; }
                ComponentDefinition? dependency = library.SingleOrDefault(d => d.Id == requirement.DefinitionId && d.Version == requirement.DefinitionVersion);
                if (dependency is not null) { Add(dependency); }
            }
            expanding.Remove((item.Id, item.Version));
        }
    }

    public ImmutableArray<DependencyChoice> UnresolvedDependencies => Snapshot.Components.SelectMany(component =>
        component.DefinitionSnapshot.DependencyRequirements.Where(requirement => !Snapshot.Dependencies.Any(e => e.DependentId == component.Id && e.RequirementId == requirement.Id) &&
            (!requirement.Optional || Snapshot.Components.Any(candidate => candidate.Id != component.Id && Matches(candidate, requirement))))
            .Select(requirement => new DependencyChoice(component.Id, requirement, Snapshot.Components.Where(c => c.Id != component.Id && Matches(c, requirement)).Select(c => c.Id).ToImmutableArray()))).ToImmutableArray();

    public void BindUnambiguous()
    {
        foreach (DependencyChoice choice in UnresolvedDependencies.Where(c => c.Candidates.Length == 1)) { Bind(choice, choice.Candidates[0]); }
    }
    public void Bind(DependencyChoice choice, Guid requiredId)
    {
        if (!choice.Candidates.Contains(requiredId)) { throw new DocumentException("/dependencies", "explicit-instance-mismatch"); }
        Snapshot = Snapshot with
        {
            Dependencies = Snapshot.Dependencies.Where(e => e.DependentId != choice.DependentId || e.RequirementId != choice.Requirement.Id).ToImmutableArray()
            .Add(new ComponentDependency(choice.DependentId, requiredId, choice.Requirement.Id, choice.Requirement.Condition, choice.Requirement.Reason))
        };
    }
    private static bool Matches(EnvironmentComponent component, DependencyRequirement requirement) =>
        component.DefinitionSnapshot.Id == requirement.DefinitionId && component.DefinitionSnapshot.Version == requirement.DefinitionVersion;
}
