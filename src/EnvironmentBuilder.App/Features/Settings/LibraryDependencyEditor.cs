using CommunityToolkit.Mvvm.ComponentModel;
using EnvironmentBuilder.App.Features.Environments;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.App.Features.Settings;

public sealed partial class LibraryDependencyEditor : EditorRow
{
    private readonly DependencyRequirement? original;
    public LibraryDependencyEditor(DependencyRequirement? requirement = null)
    {
        original = requirement;
        Id = requirement?.Id ?? "dependency-" + Guid.NewGuid().ToString("N");
        isRequired = requirement?.Optional != true;
        condition = requirement?.Condition ?? "running";
        reason = requirement?.Reason ?? "Необходим для работы сервиса";
    }
    public string Id { get; }
    public Guid? DefinitionId => SelectedService?.Definition.Id ?? original?.DefinitionId;
    public string MissingService => original is not null && SelectedService is null ? "Компонент отсутствует в библиотеке: " + original.DefinitionId : "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MissingService))] private DefinitionRow? selectedService;
    [ObservableProperty] private bool isRequired;
    [ObservableProperty] private string condition;
    [ObservableProperty] private string reason;
    public string[] Conditions { get; } = ["running", "healthy", "ready"];
    public DependencyRequirement Build(Func<DefinitionRow, ComponentDefinition>? resolve = null)
    {
        ComponentDefinition? selected = SelectedService is null ? null : resolve?.Invoke(SelectedService) ?? SelectedService.Definition;
        if (selected is null && original is null) { throw new DocumentException("/dependencyRequirements", "select-dependency"); }
        return new DependencyRequirement(Id, selected?.Id ?? original!.DefinitionId, selected?.Version ?? original!.DefinitionVersion, Condition, Reason.Trim()) { Optional = !IsRequired };
    }
}
