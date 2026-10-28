namespace EnvironmentBuilder.Core.Environments;

public static class DependencyValidator
{
    public static void Validate(IsolatedEnvironment environment)
    {
        var components = environment.Components.ToDictionary(c => c.Id);
        var edges = new HashSet<(Guid, Guid, string?)>();
        foreach (ComponentDependency edge in environment.Dependencies)
        {
            if (!components.TryGetValue(edge.DependentId, out EnvironmentComponent? dependent) ||
                !components.TryGetValue(edge.RequiredId, out EnvironmentComponent? required))
            { throw new DocumentException("/dependencies", "missing-vertex:" + edge.DependentId + "->" + edge.RequiredId); }
            if (edge.DependentId == edge.RequiredId || !edges.Add((edge.DependentId, edge.RequiredId, edge.RequirementId)))
            { throw new DocumentException("/dependencies", "duplicate-or-self-edge"); }
            if (edge.RequirementId is not null)
            {
                DependencyRequirement? requirement = dependent.DefinitionSnapshot.DependencyRequirements.FirstOrDefault(r => r.Id == edge.RequirementId);
                if (requirement is null || requirement.DefinitionId != required.DefinitionSnapshot.Id ||
                    requirement.DefinitionVersion != required.DefinitionSnapshot.Version || requirement.Condition != edge.Condition)
                { throw new DocumentException("/dependencies", "requirement-mismatch"); }
            }
            if (edge.Condition == "ready" && required.DefinitionSnapshot.Readiness.Kind == "none")
            { throw new DocumentException("/dependencies", "readiness-none"); }
        }
        foreach (EnvironmentComponent component in environment.Components)
        {
            foreach (DependencyRequirement requirement in component.DefinitionSnapshot.DependencyRequirements)
            {
                int count = environment.Dependencies.Count(e => e.DependentId == component.Id && e.RequirementId == requirement.Id);
                if (count != 1 && !(requirement.Optional && count == 0))
                { throw new DocumentException("/dependencies", "explicit-binding-required:" + component.Id + "/" + requirement.Id); }
            }
        }
        var finished = new HashSet<Guid>();
        var visiting = new List<Guid>();
        foreach (Guid id in components.Keys) { Visit(id); }
        void Visit(Guid id)
        {
            if (finished.Contains(id)) { return; }
            if (visiting.Contains(id))
            { throw new DocumentException("/dependencies", "cycle:" + string.Join("->", visiting.Append(id))); }
            visiting.Add(id);
            foreach (ComponentDependency edge in environment.Dependencies.Where(e => e.DependentId == id)) { Visit(edge.RequiredId); }
            visiting.RemoveAt(visiting.Count - 1);
            finished.Add(id);
        }
    }
}
