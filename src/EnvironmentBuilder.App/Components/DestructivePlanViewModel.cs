using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Components;

public sealed class DestructivePlanViewModel(OperationPlan plan)
{
    public OperationPlan Plan { get; } = plan;
    public string Warning => "Данные будут удалены без возможности восстановления. При частичной очистке прежняя спецификация останется сохранённой; уже удалённые данные не восстановятся.";
    public string Details => $"Окружение: {Plan.EnvironmentName}\nID: {Plan.EnvironmentId}\nРевизия: {Plan.Revision} → {Plan.Candidate?.Revision}\n" +
        $"Engine: {Plan.EngineId}\nEndpoint: {Plan.Endpoint}\nПроверено: {Plan.ObservedAt?.ToLocalTime():g}\n" +
        $"Поколение данных: {Plan.DataGenerationId} → {Plan.NextDataGenerationId}\nBaseline: {Plan.BaselineHash}\n" +
        $"План: {Plan.Id}\nПодтверждаемый hash: {Plan.Hash}\n\n" +
        $"Новое имя: {Plan.Candidate?.Name}\n" + string.Join("\n", Plan.Candidate?.Components.Select(c => $"{c.DefinitionSnapshot.Name}: {c.Id}") ?? []) + "\n\n" +
        (Plan.Resources.Length == 0 ? "Проверено отсутствие прежних ресурсов. Изменение сохранённого описания всё равно требует подтверждения.\n" :
        string.Join("\n\n", Plan.Resources.Select(resource =>
            $"{resource.Kind}: {resource.Name}\nID: {resource.ActualDockerId}\nResource UUID: {resource.ResourceId}\nКомпонент: {resource.ComponentId}\n" +
            $"Installation: {resource.InstallationId}\nРевизия создания: {resource.CreationRevision}\nПоколение: {resource.DataGenerationId}\n" +
            (resource.Volume is null ? "" : $"Том данных: {resource.Volume.CreatedAt}, {resource.Volume.Driver}, {resource.Volume.Scope}\nOptions: {System.Text.Json.JsonSerializer.Serialize(resource.Volume.Options)}\n") +
            "Потребители: " + string.Join(", ", Plan.ExpectedResources?.SingleOrDefault(e => e.Kind == resource.Kind && e.Identity == resource.ActualDockerId)?.Consumers ?? []) +
            "\nСостояние: " + (Plan.ExpectedResources?.SingleOrDefault(e => e.Kind == resource.Kind && e.Identity == resource.ActualDockerId)?.State ?? "уже отсутствует"))) + "\n") +
        "\nЭтапы:\n" + string.Join(" → ", Plan.Stages) + "\n\n" + Plan.Consequences +
        "\n\nВходы кандидата:\n" + string.Join("\n", Plan.Inputs.Select(i => i.Reference + ": " + i.Hash)) +
        "\n\nПри изменении состава или условий потребуется новый план и подтверждение.";
}
