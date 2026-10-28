namespace EnvironmentBuilder.App.Features.SeedPacks;

// In-memory examples keep unfinished surfaces reviewable. They are never persisted.
public sealed record SeedPackPresentation(string Title, string Theme, string Size, string Entities, string Version, string Status, string Icon, bool Installed, string DiskSize, int? Users, int? Events, int? Chats, int? Documents, int? Departments)
{
    public bool IsExample { get; init; }
    public Guid? PackId { get; init; }
    public string? RawVersion { get; init; }
    public string ValidationTitle => IsExample ? "Пак валиден и готов к применению" : "Метаданные пака загружены";
    public string ValidationDetail => IsExample ? "Пример проверки обязательных сущностей и зависимостей." : "Содержимое и совместимость ещё не проверены.";
    public static IReadOnlyList<SeedPackPresentation> Examples { get; } =
    [
        new("Generic Office Starter", "Office", "Medium", "48 / 320 / 14 / 27", "v1.4.0", "Установлен", "BriefcaseBusiness", true, "18.4 MB", 48, 320, 14, 27, 6) { IsExample = true },
        new("Generic Office Extended", "Office", "Large", "120 / 850 / 42 / 73", "v1.3.0", "Установлен", "BriefcaseBusiness", true, "62.8 MB", 120, 850, 42, 73, 12) { IsExample = true },
        new("Fantasy Fellowship Demo", "Fantasy", "Large", "80 / 410 / 26 / 52", "v1.2.0", "Загружен", "Leaf", false, "43.2 MB", 80, 410, 26, 52, 8) { IsExample = true },
        new("Wizard School Demo", "Fantasy", "Medium", "64 / 280 / 18 / 36", "v0.9.1", "Черновик", "FlaskConical", false, "24.6 MB", 64, 280, 18, 36, 5) { IsExample = true },
        new("Space Rebels Demo", "Sci-Fi", "Large", "96 / 520 / 38 / 61", "v1.1.0", "Установлен", "Rocket", true, "56.1 MB", 96, 520, 38, 61, 7) { IsExample = true },
        new("Enterprise Support Team", "Office", "Medium", "75 / 310 / 27 / 43", "v1.0.2", "Загружен", "Users", false, "31.4 MB", 75, 310, 27, 43, 6) { IsExample = true },
        new("Calendar Stress Test 10k", "Office", "XL", "500 / 10000 / 120 / 210", "v2.0.0", "Установлен", "CalendarDays", true, "142.3 MB", 500, 10000, 120, 210, 24) { IsExample = true },
        new("Minimal Smoke Pack", "Generic", "Small", "10 / 25 / 5 / 8", "v0.5.0", "Загружен", "FileText", false, "3.2 MB", 10, 25, 5, 8, 2) { IsExample = true }
    ];
}

