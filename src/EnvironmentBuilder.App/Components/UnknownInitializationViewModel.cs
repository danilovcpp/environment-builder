using CommunityToolkit.Mvvm.ComponentModel;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.App.Components;

public sealed partial class UnknownInitializationViewModel(IsolatedEnvironment environment, DataApplicationRecord record) : ObservableObject
{
    public string Details => $"{environment.Name}\nОкружение: {environment.Id}\nЭкземпляр: {record.Key.ComponentId}\nШаг: {record.Key.StepId} · {record.State} · попытка {record.Attempt}\nПоколение: {record.Key.DataGenerationId}\nApplication SHA-256: {record.Key.ApplicationHash}\nIdempotency key: {record.Key.StableIdempotencyKey}";
    public string Warning => "Сервис мог применить запрос до потери ответа. Повтор может создать дубликаты. Признание успеха допустимо только после независимой проверки результата в сервисе. Состояние контейнера не доказывает бизнес-результат. Refresh удалит данные только после отдельного подтверждения точного плана; сначала остановите окружение.";
    [ObservableProperty] private bool independentlyVerified;
    public InitializationDecision? Decision { get; set; }
}
public interface IUnknownInitializationInteractions
{
    Task<UnknownInitializationViewModel?> DecideAsync(IsolatedEnvironment environment, DataApplicationRecord record, CancellationToken token);
}
