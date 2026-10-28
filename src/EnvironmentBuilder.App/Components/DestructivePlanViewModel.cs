using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.App.Components;

public sealed class DestructivePlanViewModel(OperationPlan plan)
{
    public OperationPlan Plan { get; } = plan;
    private bool IsDeletion => Plan.Stages.Contains("close-description");
    public string Title => IsDeletion ? "Удалить окружение" : Plan.Candidate is not null ? "Удалить прежние данные и сохранить новую ревизию" : Plan.TemporaryResources.IsDefaultOrEmpty ? "Очистить данные окружения" : "Восстановить baseline (refresh данных)";
    public string ConfirmLabel => IsDeletion ? "Удалить окружение и данные" : Plan.Candidate is not null ? "Удалить данные и сохранить" : Plan.TemporaryResources.IsDefaultOrEmpty ? "Удалить данные" : "Удалить данные и восстановить baseline";
    public string Warning => "Данные будут удалены без возможности восстановления. При частичной очистке прежняя спецификация останется сохранённой; уже удалённые данные не восстановятся.";
    public string Details => $"Окружение: {Plan.EnvironmentName}\nID: {Plan.EnvironmentId}\nРевизия: {Plan.Revision}" + (IsDeletion ? " (описание удаляется)\n" : Plan.Candidate is null ? " (сохраняется)\n" : $" → {Plan.Candidate.Revision}\n") +
        $"Engine: {Plan.EngineId}\nEndpoint: {Plan.Endpoint}\nПроверено: {Plan.ObservedAt?.ToLocalTime():g}\n" +
        $"Поколение данных: {Plan.DataGenerationId}" + (IsDeletion ? " (удаляется)\n" : $" → {Plan.NextDataGenerationId}\n") + $"Baseline: {Plan.BaselineHash}\n" +
        $"План: {Plan.Id}\nПодтверждаемый hash: {Plan.Hash}\n\n" +
        (Plan.Candidate is null ? "" : $"Новое имя: {Plan.Candidate.Name}\n" + string.Join("\n", Plan.Candidate.Components.Select(c => $"{c.DefinitionSnapshot.Name}: {c.Id}")) + "\n\n") +
        (Plan.Resources.Length == 0 ? "Проверено отсутствие прежних ресурсов. Операция всё равно требует подтверждения.\n" :
        string.Join("\n\n", Plan.Resources.Select(resource =>
            $"{resource.Kind}: {resource.Name}\nID: {resource.ActualDockerId}\nResource UUID: {resource.ResourceId}\nКомпонент: {resource.ComponentId}\n" +
            $"Installation: {resource.InstallationId}\nРевизия создания: {resource.CreationRevision}\nПоколение: {resource.DataGenerationId}\n" +
            (resource.Volume is null ? "" : $"Том данных: {resource.Volume.CreatedAt}, {resource.Volume.Driver}, {resource.Volume.Scope}\nOptions: {System.Text.Json.JsonSerializer.Serialize(resource.Volume.Options)}\n") +
            "Потребители: " + string.Join(", ", Plan.ExpectedResources?.SingleOrDefault(e => e.Kind == resource.Kind && e.Identity == resource.ActualDockerId)?.Consumers ?? []) +
            "\nСостояние: " + (Plan.ExpectedResources?.SingleOrDefault(e => e.Kind == resource.Kind && e.Identity == resource.ActualDockerId)?.State ?? "уже отсутствует"))) + "\n") +
        "\nЭтапы:\n" + string.Join(" → ", Plan.Stages) + "\n\n" + Plan.Consequences +
        "\n\nПроверенные входы:\n" + string.Join("\n", Plan.Inputs.Select(i => i.Reference + ": " + i.Hash + " " + i.RuntimeIdentity)) +
        (Plan.TemporaryResources.IsDefaultOrEmpty ? "" : "\n\nВременный запуск и последующая остановка:\n" + string.Join("\n", Plan.TemporaryResources.Select(r => $"{r.Kind}: {r.Name}; UUID {r.ResourceId}; компонент {r.ComponentId}"))) +
        (Plan.TemporaryPorts.IsDefaultOrEmpty ? "" : "\n\nВременные порты:\n" + string.Join("\n", Plan.TemporaryPorts.Select(p => $"{p.ComponentId}: {p.Port.HostIp}:{p.Port.Published?.ToString() ?? "auto"} → {p.Port.Target}/{p.Port.Protocol}; control={p.Port.AutomaticControl}"))) +
        "\n\nПри изменении состава или условий потребуется новый план и подтверждение.";
}
