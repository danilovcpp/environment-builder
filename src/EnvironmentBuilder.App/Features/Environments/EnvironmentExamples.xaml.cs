using System.Windows.Controls;
using EnvironmentBuilder.App.Components;

namespace EnvironmentBuilder.App.Features.Environments;

public sealed record EnvironmentExample(string Name, string Description, bool Running, string Cpu, string Memory)
{
    public string Status => Running ? "Запущен" : "Остановлен";
    public string StatusColor => Running ? "#279C66" : "#98A2B4";
    public bool InitiallyExpanded => Name == "eoffice6-dev";
    public static IReadOnlyList<EnvironmentExample> Items { get; } =
    [
        new("eoffice6-dev", "Разработка новой версии", true, "0.92%", "406 MB"),
        new("test", "Тестирование изменений", false, "—", "—"),
        new("демо-календарь", "Демо-окружение для презентаций", true, "0.24%", "128 MB"),
        new("feature-x", "Задача EOFFICE-1234", false, "—", "—")
    ];
    public static IReadOnlyList<ServicePresentation> Services { get; } =
    [ServicePresentation.Examples[4], ServicePresentation.Examples[8], ServicePresentation.Examples[12], ServicePresentation.Examples[13], ServicePresentation.Infrastructure[0], ServicePresentation.Infrastructure[1]];
}

public partial class EnvironmentExamples : UserControl
{
    public EnvironmentExamples() { InitializeComponent(); }
}
