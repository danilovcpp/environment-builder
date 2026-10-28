# Phase 3 / US1 verification

Проверено 2026-10-23. Выполнена только Phase 3, T037–T050. Phase 4 и последующие фазы не выполнялись.

## Реализованное поведение

- Новый черновик получает собственные environment/draft/instance UUID. Defaults материализуются один раз, обязательная initialization сохраняется snapshot в baseline. Точная зависимость включается из библиотеки с причиной; несколько подходящих экземпляров требуют явного выбора.
- Редактор содержит metadata, image/build source, typed parameters, literal/secret-reference environment, порты с областью доступа, managed/read-only storage, instance dependencies и ordered baseline с exact package references. Структурные ошибки блокируют Save; отдельная проверка входов показывает readiness issues. Выбор локальной папки сохраняет mapping без выполнения содержимого.
- Создание сохраняет revision 1, generation, references и summary атомарно. Ошибка записи оставляет черновик и последнее корректное состояние; недоступные paths/secrets/images/packs не подменяются defaults. Сохранённое описание открывается для просмотра; изменение существующей конфигурации остаётся отдельным destructive workflow последующих фаз.
- Чистый compiler формирует детерминированную Compose JSON/YAML-подмножество модель с UUID-based именами, canonical typed bindings, экранированием `$`, отдельными managed volumes, read-only staging placeholders и явными host IP. При необходимости добавляется auto loopback control mapping. Preview и runtime render используют одну модель; runtime render сам ничего не запускает.
- Preview token связывает effective configuration, revision, baseline/initializer/pack hashes, secret-reference IDs, build/read-only content fingerprints и известные image identities. Файлы хешируются без исполнения; неизвестный image не вызывает lookup. Изменение bytes или ignore-файла меняет token. Вычисленные native paths и секретные значения в token не входят.
- Список различает loading/empty/no-results/store error/unreadable row/unknown runtime, поддерживает поиск, status/type filters, expansion, selection и scroll state. Новый saved item выделяется; при скрывающих его фильтрах показывается объяснение. Неизвестные метрики не заменяются нулём.
- Вложенные editor/preview routes сохраняют экземпляр VM и контекст возврата. Dirty dialog поддерживает Save/Discard/Stay; Escape и close означают Stay. Неудачный Save не навигирует. Guard действует также при переходе в другой основной раздел и закрытии окна.

## Проверки

| Проверка | Результат |
| --- | --- |
| Locked restore / NuGet audit | PASS; новые packages не добавлялись |
| Release build, analyzers, warnings as errors | PASS, 0 warnings / 0 errors |
| Unit tests | 47 passed, 0 failed, 0 skipped |
| Offline integration | 77 passed, 0 failed, 0 skipped |
| Resources / authoring views smoke | 1 passed, 0 failed, 0 skipped |
| Реальное окно: FlaUI keyboard smoke | 1 passed, 0 failed, 0 skipped |
| dotnet format --verify-no-changes --no-restore | PASS |
| git diff --check | PASS |
| Extension hooks | `.specify/extensions.yml` отсутствует до и после реализации |

Итоговые TRX и PNG: `artifacts/authoring/final/`. Resources test создаёт скомпилированный WPF shell и все вкладки editor при 1280×720 DIP, переключает Light/Dark без замены VM, проверяет отсутствие binding errors и переход error-summary → parameter tab/focus. PNG рендерятся непосредственно из visual tree.

FlaUI запускает отдельную копию приложения с уникальным временным store, предварительно заполненным production component reader/library store. Проверены keyboard Create → ввод имени → component selection → добавление target с auto dependency → theme switch → preview → возврат в тот же draft → Escape/Stay → Save из dirty dialog → reopen → Discard другого draft. После завершения процесса приложение запускается снова и повторно открывает сохранённое описание и Compose. В SQLite подтверждены revision 1, два экземпляра и отсутствие runtime operations.

Offline suite проверяет отдельный prepared fixture с неизвестными приложению Atlas Worker/Copper Queue, missing inputs, package readiness, actual byte fingerprints, unknown/known image identities и failure before commit. `FixtureStoreBuilder` создаёт 1 000 окружений через production readers/store; поиск по описанию возвращает ожидаемые 500 записей, измерения остаются unavailable. Этот тест не является полной performance qualification SC-002/003.

Authoring/compiler/capture зависят только от document/storage/input boundaries; runtime/process/HTTP transport не входят в их зависимости. Save/import/preview не создают operation/ledger records и не исполняют Dockerfile. Проверки используют контролируемые границы ввода-вывода.

## Команды

Из корня репозитория, с локальным NuGet cache:

```powershell
dotnet restore EnvironmentBuilder.sln --locked-mode -m:1 -nr:false
dotnet build EnvironmentBuilder.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -warnaserror
dotnet test tests/EnvironmentBuilder.UnitTests/EnvironmentBuilder.UnitTests.csproj -c Release --no-build --no-restore -m:1 -nr:false
dotnet test tests/EnvironmentBuilder.IntegrationTests/EnvironmentBuilder.IntegrationTests.csproj -c Release --no-build --no-restore -m:1 -nr:false
dotnet test tests/EnvironmentBuilder.UiTests/EnvironmentBuilder.UiTests.csproj -c Release --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~ThemeResourcesTests'
dotnet test tests/EnvironmentBuilder.UiTests/EnvironmentBuilder.UiTests.csproj -c Release --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~AuthoringSmokeTests'
dotnet format EnvironmentBuilder.sln --verify-no-changes --no-restore
git diff --check
```

Для ACL/DPAPI, interactive desktop и MSBuild formatter named pipe использован разрешённый запуск вне sandbox. Обычное пользовательское хранилище не открывалось. Полная DPI/keyboard-матрица всех будущих экранов, runtime-сценарии последующих историй и release acceptance остаются за пределами US1.
