# environment-builder

## Состояние реализации

Реализованы Setup, Foundation и истории US1–US7: описание и сохранение окружений,
Compose preview, Start, Inspect, Stop, повторный Start, изменение с новой ревизией,
очистка данных, восстановление baseline, удаление окружений и библиотека SeedPack.
Также реализована основная цепочка настройки собственных сервисов (US8).
Последующие возможности остаются в работе;
полная поставка ещё не завершена.

Основной сценарий:

1. В разделе «Сервисы» нажать «Добавить сервис» или «Добавить инфраструктуру», указать имя, источник, иконку и цвет. Для целевого сервиса можно выбрать `build`, папку исходников и зависимости из уже добавленных компонентов библиотеки. Обязательные зависимости добавляются в среду автоматически; необязательные можно включить отдельно.
2. Для инфраструктуры начать вводить имя образа: появятся результаты поиска Docker Hub. Выбор образа загружает версии и логотип при наличии; тег можно выбрать из списка или ввести вручную. Галочка «Показывать при создании окружения» управляет доступностью в каталоге. Нажать «Сохранить библиотеку».
3. В «Окружениях» нажать «Создать», выбрать сохранённые сервисы галочками и настроить env-переменные, порты, хранилища и параметры сборки отдельно для этой среды.
4. Нажать «Сохранить» или «Сохранить и запустить». При запуске открывается Inspect с фактическими состояниями контейнеров, ходом операции, логами и адресами.

Папка для build задаёт корень исходников. Контекст, Dockerfile, аргументы и секреты сборки задаются в редакторе окружения. Библиотека содержит описание источника и внешний вид сервиса; каждое окружение сохраняет собственные параметры запуска. Импортированные описания прежнего формата и существующие окружения сохраняют свои настройки.

Настройки приложения открываются отдельным пунктом внизу меню. Тема (светлая, тёмная или как в Windows), начальная страница и показ описаний окружений сохраняются автоматически в `preferences.json` внутри локального хранилища.

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
