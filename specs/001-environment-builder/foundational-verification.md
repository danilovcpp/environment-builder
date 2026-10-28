# Phase 2 verification

Проверено 2026-10-23. Объём: только Phase 2, T008–T036. Phase 3 и последующие фазы не выполнялись.

## Результат

Реализованы immutable typed snapshots документов v1, строгий UTF-8 reader, shape/domain validators, canonical hashes, instance-addressed DAG и baseline. Missing inputs остаются readiness issues; доказанный конфликт доступного пакета отклоняется как semantic error. Typed integers проверяются без потери точности, arbitrary inline JSON сохраняет числовые лексемы.

Добавлены приватный data directory и single-writer mutex, SQLite migrations/worker, atomic environment/library commits, journal, ledger, generations и application records. Intent и result имеют отдельные durable commits; неизвестный исход и неудачный финальный commit блокируют дальнейшее исполнение. Coordinator удерживает операции независимо от экранов, исключает повторную submission и достигает safe point при отмене.

Добавлены DPAPI CurrentUser vault, disposable secret context, redaction обычных/escaped/chunked значений до sinks и ограниченные diagnostic files. Local path resolver отклоняет traversal, alternate streams и reparse escapes. ZIP importer проверяет declarations, размеры, actual bytes/hashes/counts, links/executable metadata; NDJSON индексируется по byte offsets. Promotion, DB references и active leases защищают immutable payloads.

Общий shell содержит четыре маршрута, PageHeader/ContextPanel/StatusBar, Lucide navigation, начальный список и storage/recovery states. Light/Dark меняются без пересоздания ViewModel. Неизвестный runtime не объявляется stopped или ready.

## Изменение published schemas

Конечный `$` в pattern допускал финальный перевод строки. Он заменён на `(?![\s\S])`, чтобы reference/key allowlists действительно запрещали такой суффикс. Формат остаётся v1. Runtime shape checks генерируются из локальных схем; schema engine и network resolver не входят в приложение.

## Выполненные проверки

| Проверка | Результат |
| --- | --- |
| Locked restore и NuGet audit | PASS, без предупреждений; сетевой запуск явно разрешён пользователем |
| Release build, analyzers, warnings as errors | PASS, 0 warnings / 0 errors |
| Unit tests | 36 passed, 0 failed, 0 skipped |
| Offline integration | 72 passed, 0 failed, 0 skipped |
| UI resources и foundational shell smoke | 1 passed, 0 failed, 0 skipped |
| Runtime/schema parity | 6 119 случаев; локальный Draft2020-12 resolver, без remote resolution |
| Native SQLite из test runtime | 3.53.3, resolved SQLitePCLRaw.lib.e_sqlite3 2.1.13 |
| dotnet format --verify-no-changes --no-restore | PASS |
| git diff --check | PASS |
| Extension hooks | .specify/extensions.yml отсутствует до и после implementation |

TRX и Light/Dark PNG: `artifacts/foundational/verification/`. Предыдущие промежуточные прогоны не являются итоговым evidence.

Storage/DPAPI tests используют уникальные временные каталоги. Для установки ACL CurrentUser и работы MSBuild formatter named pipe потребовался запуск вне sandbox; обычное пользовательское хранилище не использовалось. Corrupt/unsupported DB проверены без замены файла; faults проверены до/после intent/effect/observation/result, включая потерю результата и отказ writer.

Основные команды (из корня репозитория, с локальным NuGet cache):

```powershell
dotnet restore EnvironmentBuilder.sln --locked-mode -m:1 -nr:false
dotnet build EnvironmentBuilder.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -warnaserror
dotnet test tests/EnvironmentBuilder.UnitTests/EnvironmentBuilder.UnitTests.csproj -c Release --no-build --no-restore -m:1 -nr:false
dotnet test tests/EnvironmentBuilder.IntegrationTests/EnvironmentBuilder.IntegrationTests.csproj -c Release --no-build --no-restore -m:1 -nr:false
dotnet test tests/EnvironmentBuilder.UiTests/EnvironmentBuilder.UiTests.csproj -c Release --no-build --no-restore -m:1 -nr:false --filter 'Category=UI'
dotnet format EnvironmentBuilder.sln --verify-no-changes --no-restore
git diff --check
```

## Границы подтверждения

Пользовательские workflow следующих историй и полная keyboard/DPI-матрица не запускались и не объявляются пройденными. UI smoke проверяет скомпилированные resources, маршруты, сохранение VM, collapse/expand панели и binding errors; он не заменяет полную интерактивную приёмку следующих фаз.
