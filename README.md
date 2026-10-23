# environment-builder

## Состояние реализации

Выполнена Phase 1 (Setup), T001–T007: solution из трёх production-проектов и трёх test-проектов,
зафиксированные NuGet-зависимости, тестовая инфраструктура, ресурсы Light/Dark и общие стили WPF.
Пользовательские экраны и runtime ещё не реализованы; приложение пока не предназначено для использования.

Для разработки требуется Windows x64 и .NET SDK 10.0.402 (C# 14). Проверки из корня репозитория:

```powershell
dotnet restore EnvironmentBuilder.sln --locked-mode
dotnet build EnvironmentBuilder.sln --configuration Release --no-restore -warnaserror
dotnet test tests/EnvironmentBuilder.UnitTests/EnvironmentBuilder.UnitTests.csproj --configuration Release --no-build --no-restore --logger trx
dotnet test tests/EnvironmentBuilder.IntegrationTests/EnvironmentBuilder.IntegrationTests.csproj --configuration Release --no-build --no-restore --filter 'Category!=Docker' --logger trx
dotnet test tests/EnvironmentBuilder.UiTests/EnvironmentBuilder.UiTests.csproj --configuration Release --no-build --no-restore --filter 'Category=UI' --logger trx
dotnet format EnvironmentBuilder.sln --verify-no-changes --no-restore
```

Существующий UI-тест проверяет общие ресурсы на STA-потоке и рендерит контролы в обеих темах;
он не заменяет будущую интерактивную приёмку экранов, клавиатуры и DPI.
Для сохранения PNG задайте `ENVIRONMENTBUILDER_UI_ARTIFACTS` абсолютным путём к каталогу результатов.
Устройство тестовых стендов описано в [tests/README.md](tests/README.md).

## GitHub Spec Kit

В проекте настроен Spec Kit 1.2.3 для Codex со скриптами PowerShell.
Навыки находятся в `.agents/skills/`, шаблоны и workflow — в `.specify/`.

Запускайте навыки в чате Codex из папки проекта:

1. `$speckit-constitution` — принципы проекта.
2. `$speckit-specify` — спецификация функциональности.
3. `$speckit-plan` — план реализации.
4. `$speckit-tasks` — список задач.
5. `$speckit-implement` — реализация.

Локальный CLI установлен в `.tools/spec-kit/` и исключён из Git:

```powershell
.\.tools\spec-kit\Scripts\specify.exe version
.\.tools\spec-kit\Scripts\specify.exe check
```

Для восстановления CLI после клонирования (Python 3.11+):

```powershell
python -m venv .tools/spec-kit
.\.tools\spec-kit\Scripts\python.exe -m pip install specify-cli==1.2.3
```

Документация: [GitHub Spec Kit](https://github.com/github/spec-kit).
