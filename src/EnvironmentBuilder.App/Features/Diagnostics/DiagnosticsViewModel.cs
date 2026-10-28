using CommunityToolkit.Mvvm.ComponentModel;

namespace EnvironmentBuilder.App.Features.Diagnostics;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    [ObservableProperty] private string storageStatus = "Хранилище ещё не проверено";
    [ObservableProperty] private string recoveryStatus = "Незавершённые операции ещё не прочитаны";
    [ObservableProperty] private string error = "";
    public string RuntimeStatus => "Docker не проверен. Unknown не означает остановку или готовность. Фактическая проверка выполняется в Inspect выбранного окружения.";
    public string WorkflowReason => "Детальная сверка, действия восстановления и экспорт диагностики пока недоступны в этой версии. Операции не повторяются автоматически.";
}
