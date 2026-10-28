# environment-builder

## Состояние реализации

Реализованы Setup, Foundation и истории US1–US3: описание и сохранение окружений,
Compose preview, Start, Inspect, Stop и повторный Start. Последующие истории остаются в работе;
полная поставка ещё не завершена.

Для разработки требуется Windows x64 и .NET SDK 10.0.402 (C# 14).

Приложение не проверяет выпуск или номер сборки Windows. Для запуска окружений нужны
доступные Docker Desktop/WSL2 и Linux-контейнеры; их возможности проверяются при запуске операции.
Автоматические тесты используют изолированные хранилища и подмены Docker/HTTP, без запуска внешних сервисов.

Проверки из корня репозитория:

```powershell
dotnet restore EnvironmentBuilder.sln --locked-mode
dotnet build EnvironmentBuilder.sln --configuration Release --no-restore -warnaserror
dotnet test tests/EnvironmentBuilder.UnitTests/EnvironmentBuilder.UnitTests.csproj --configuration Release --no-build --no-restore --logger trx
dotnet test tests/EnvironmentBuilder.IntegrationTests/EnvironmentBuilder.IntegrationTests.csproj --configuration Release --no-build --no-restore --logger trx
dotnet test tests/EnvironmentBuilder.UiTests/EnvironmentBuilder.UiTests.csproj --configuration Release --no-build --no-restore --filter 'Category=UI' --logger trx
dotnet format EnvironmentBuilder.sln --verify-no-changes --no-restore
```

UI-тесты проверяют общие ресурсы, authoring и Inspect, обе темы и основные клавиатурные сценарии;
полная матрица экранов и DPI относится к последующим задачам.
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
