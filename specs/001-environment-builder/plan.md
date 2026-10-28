# Implementation Plan: Environment Builder — основная версия

**Branch**: `main` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Updated**: 2026-10-28 — доработка UI Phase 7a выполнена; следующий функциональный этап — US6 / Phase 8.

**Input**: `specs/001-environment-builder/spec.md`, [source.md](source.md), [конституция](../../.specify/memory/constitution.md) v1.0.0 и [style guide](../../docs/design/style-guide.md).

`setup-plan.ps1 -Json` вернул `BRANCH=001-environment-builder`: это feature identifier из имени каталога. Фактическая Git-ветка — `main`; план не создаёт и не переключает ветки.

## Summary

Создать локальное WPF-приложение для описания, запуска, наблюдения и безопасного управления данными изолированных Linux-контейнерных окружений. Все US1–US10 входят в поставку; P1 определяет последовательность, а не сокращение объёма. Сохранение и preview работают без Docker. Любое изменение сохранённого окружения, включая имя, требует подтверждённой остановки и очистки прежних ресурсов перед новой ревизией.

Подход: .NET 10 / WPF UI / MVVM; три производственных проекта с границами UI, правил приложения и внешних эффектов. Строгие JSON-документы v1 сохраняются вместе с журналом операций в SQLite. Один Docker CLI adapter выполняет Compose и адресные действия/наблюдение по проверенным ID. Универсальный интерпретатор декларативных HTTP-запросов применяет baseline без сервисных имён в коде. Изменяющие операции журналируются до внешнего эффекта, сериализуются по ID окружения и сверяются после прерывания.

<a id="ui-refinement"></a>

## Доработка UI по референсам (Phase 7a, завершена)

**Порядок**: завершённая US5 / Phase 7 → **Phase 7a, T166–T177** в [tasks.md](tasks.md#phase-7a-ui-refinement) → US6 / Phase 8 → US7–US10 → итоговая приёмка. Phase 7a — обязательный checkpoint, завершённый в текущем запуске; следующий функциональный этап — US6 / Phase 8. Существующие ID задач и результаты проверок сохраняются.

**Визуальный источник истины**: [seed_packs.png](../../docs/design/mockups/seed_packs.png). При расхождениях между референсами или с прежним style guide брать из него общую компоновку, оформление и цвета: пропорции shell/content/context, поверхности, заголовки, таблицы, отступы, типографическую иерархию, кнопки, иконки и бейджи. [environments.png](../../docs/design/mockups/environments.png), [create_edit.png](../../docs/design/mockups/create_edit.png), [inspect.png](../../docs/design/mockups/inspect.png) и [settings.png](../../docs/design/mockups/settings.png) определяют состав и назначение областей своих экранов; их визуальные расхождения нормализуются по `seed_packs.png`. Светлая тема воспроизводит этот эталон, тёмная сохраняет ту же геометрию и иерархию с semantic Dark tokens из style guide. Доступность по QR-002/003 обязательна; необходимые отклонения фиксируются с причиной.

Спецификация определяет функции, данные и последствия действий: неизменяемые ID, сохранение без запуска, только внешний импорт SeedPack, контекстный Compose preview и подтверждения опасных операций. Изображённые создание/редактирование паков, произвольные метрики и демонстрационные записи не становятся функциями или данными приложения.

### Объём этапа

1. Зафиксировать в `docs/design/ui-reference.md` соответствие экранов референсам, общие размеры/цвета/состояния и перечень отличий текущего UI. Согласовать существующие `style-guide.md` и theme tokens с основным референсом; не ограничиваться сменой палитры.
2. Переработать общие Shell, PageHeader, ContextPanel, StatusBar и стили: ведущие действия в заголовке, единая навигация, компактные таблицы, фильтры, иконки действий, сводки и читаемые статусы.
3. Привести список окружений, create/edit, Inspect, Compose preview и все существующие диалоги к референсам. Сохранить виртуализацию, команды, контекст возврата, draft/selection/scroll/progress и результаты US1–US5.
4. Заменить текстовые заглушки SeedPacks/Settings/Diagnostics структурой будущих экранов в общем визуальном языке. Использовать доступные production-данные; ещё не реализованные функции показывать недоступными с причиной. Полноценные сценарии US7/US8/US10 остаются задачами этих историй и подключаются к подготовленным представлениям. Наполненные test fixtures нужны для визуальной проверки; они не входят в пользовательское хранилище и не заменяют production-интеграцию.
5. Проверить внешний вид на сопоставимых скриншотах каждого экрана, обе темы и состояния; устранить расхождения до закрытия этапа. Последующие UI задачи продолжают пользоваться этими компонентами и визуальной проверкой.

### Приёмка Phase 7a

**Уточнение текущего запуска, 2026-10-28:** по прямому указанию пользователя
«не надо проверять при других масштабах» проверка выполнена при текущих 150%
(actual DPI 144), без изменения системного масштаба. Требование дополнительных
масштабов ниже для этого запуска снято; будущая release-матрица T158 не закрывается.
Результаты: [ui-reference.md](../../docs/design/ui-reference.md) и
`artifacts/ui-refinement/20261028/verification.md`.

- В `artifacts/ui-refinement/<runId>/` сохранены screenshots до/после, сравнения с референсами при одинаковом размере окна и фактическом DPI, `reference-review.md` с результатом по каждому экрану и `verification.md` с командами/исходами. Для экранов без собственного макета сравниваются общие паттерны `seed_packs.png`.
- Проверено соответствие геометрии, плотности строк, иерархии, цветов, отступов, иконок, фильтров, команд и контекстных сводок; отклонения допускаются только для требований поведения, доступности и адаптации к размеру окна и явно объясняются. Большие вертикальные карточки вместо предусмотренных компактных таблиц не проходят приёмку.
- Интерактивно проверены Light/Dark при 1280×720 DIP и размере основного референса, 100% и как минимум одном повышенном фактическом DPI из QR-003; keyboard/focus, длинные имена, empty/loading/error/unknown/offline/partial и состояния контролов. Полная release DPI-матрица по всем завершённым историям остаётся T158.
- Release build, затронутые automated suites и UI smoke проходят без binding/resource errors; theme switch и операции сохраняют своё состояние. Одних зелёных тестов ресурсов или наличия PNG недостаточно: требуется просмотр и зафиксированное сравнение с референсами.
- Checkpoint закрывается только после исправления визуальных расхождений и проверенного результата T177; отсутствующая интерактивная проверка остаётся Unverified. Новые задачи не отмечены выполненными на этапе планирования.

## Technical Context

**Language/Version**: C# 14, .NET 10 LTS; SDK 10.0.402 в `global.json`, `rollForward=latestPatch`; Core — `net10.0`, WPF и Windows infrastructure — `net10.0-windows`; RID `win-x64`.

**Primary Dependencies**: WPF-UI 4.3.0; CommunityToolkit.Mvvm 8.4.2; MahApps.Metro.IconPacks.Lucide 6.2.1; Microsoft.Extensions.DependencyInjection, Microsoft.Data.Sqlite и System.Security.Cryptography.ProtectedData 10.0.13. Central Package Management и lock files без wildcard. JSON, ZIP, HTTP, channels, process и hashing — .NET BCL. Runtime JSON Schema library, Docker SDK, GenericHost и общий YAML-парсер не нужны. Назначение зависимостей и alternatives — [research.md](research.md).

**Storage**: `%LOCALAPPDATA%/EnvironmentBuilder/`: SQLite `state.db` (JSON-описания, библиотека, refs, defaults, journal и resource ledger); immutable SeedPack blobs по SHA-256; приватные временные runtime-файлы; DPAPI CurrentUser для секретов. DB schema version независима от external format version. SQLite работает на одном выделенном persistence worker, поскольку его ADO async APIs исполняются синхронно; короткие transactions, WAL + FULL и проверка исправленной native SQLite.

**Testing**: xUnit v3 4.0.2, xunit.runner.visualstudio 4.0.1, Microsoft.NET.Test.Sdk 18.11.0; Core/VM без окон; integration с изолированными stores и Docker/HTTP doubles, contract fixtures и fault injection до/после изменяющих этапов; FlaUI.UIA3 5.0.0 и ручная UI проверка. Запуск Docker и внешних сервисов в тестах не требуется. Команды и матрица SC — [quickstart.md](quickstart.md).

**Target Platform**: Windows x64 с WSL2 и локальным Docker Desktop, Linux containers. Приложение не ограничивает выпуск или номер сборки Windows. Минимум Engine 28.0.0 / API 1.48 и Compose 2.24.6; допускаются Compose major 2 и 5 после capability probe. Доступность runtime определяется фактическими возможностями Docker. Remote endpoint, Windows containers и неподдерживаемые capabilities блокируют runtime, но не authoring/preview.

**Project Type**: Windows desktop application; отдельный demo из трёх ASP.NET Core сервисов и RabbitMQ.

**Performance Goals**: SC-002/003/009/010: 1 000 окружений, список ≤3 s; поиск/filter ≤1 s в 95/100; видимое начало и навигация ≤1 s в 95/100; первая preview-page 100 000 записей ≤2 s; час логов ≥100 lines/s и 100 переключений без роста подписок. Pull/build/readiness измеряются отдельно.

**Constraints**: offline authoring; redaction до каждого UI/storage sink; импорт/preview не запускают процессы или HTTP; внешние build inputs только для чтения; no prune; exact ownership; no rollback promise. Bounds: logs 10 000 строк / 8 MiB на активное наблюдение и 32 MiB на приложение, одна строка 16 KiB, 300 metric samples/component, preview 100 records / 256 KiB/page, record ≤64 KiB. Детали — [runtime.md](contracts/runtime.md).

**Scale/Scope**: один локальный пользователь, один writer на data directory, 1 000 описаний; испытательный состав до 20 компонентов/окружение и две одновременно работающие demo-копии. Это размеры стенда, а не запрет большего состава. Четыре основных раздела, вложенные editor/Inspect/Compose, две темы. Reference bench для измерений: Windows x64, ≥8 logical CPUs, 32 GiB RAM, SSD с ≥30 GiB свободного места, Docker VM 4 CPUs / 8 GiB. Реальные характеристики и версии записываются перед замерами; характеристики ещё не измерены.

## Constitution Check

*GATE: оценено до фиксации Phase 0 решений и повторно после Phase 1. PASS означает соответствие дизайна; это не результат проверки будущего продукта.*

| Принцип / gate | До исследования | После дизайна / свидетельство |
| --- | --- | --- |
| I: читаемость, минимальность | PASS: кодовой базы для переиспользования нет | PASS: ordinary services/records; один validator/compiler; без CQRS/шины/plugins |
| II: границы | PASS: разделить UI/Core/I/O | PASS: App → Core; Infrastructure → Core; App composition root → Infrastructure; отдельная Domain assembly не нужна |
| III: MVVM / UI decisions | PASS: WPF UI и Lucide обязательны | PASS: commands/state во VM; view-only focus/close bridge; общие PageHeader/ContextPanel/StatusBar/tokens; [ui.md](contracts/ui.md) |
| IV: abstractions | PASS: только реальные seams | PASS: runtime/storage/secrets/HTTP/process/clock/dialog boundaries изолируют эффекты; нет интерфейса для каждой модели |
| V: threading/lifetimes | PASS: операции переживают страницу | PASS: app-owned coordinator, per-environment lock, async I/O, persistence worker, bounded channels, disposal, safe close |
| VI: tests | PASS: нужны behavior/VM/integration/UI | PASS: faults, secrets, isolation, replay и UI matrix заданы в quickstart |
| VII: compatibility/debt | PASS: прежних contracts нет | PASS: format v1, strict versions, secret refs, отсутствие подстановки defaults; зависимости обоснованы |
| VIII: verification | PASS: прочитаны spec/source/style/структура | PASS по дизайну: команды и критерии определены; build/tests/runtime реализации ещё НЕ выполнялись |
| Quality: requirements/errors | PASS: US1–10, FR-001–041 в scope | PASS: SC-001–013 связаны с проверками; partial/unknown/tombstone определены |
| Quality: handoff | PASS: exception не требуется | PASS: нет принятых нарушений; ограничения среды раскрыты |

## Project Structure

### Documentation (this feature)

```text
specs/001-environment-builder/
├── spec.md
├── source.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
└── contracts/
    ├── documents.md
    ├── runtime.md
    ├── ui.md
    ├── common.schema.json
    ├── environment.schema.json
    ├── component.schema.json
    ├── initialization.schema.json
    ├── seedpack.schema.json
    └── examples/
```

Исполнимый порядок поддерживается в существующем [tasks.md](tasks.md); обновление 2026-10-28 добавляет Phase 7a без перенумерации исторических задач.

### Source Code (repository root — planned)

```text
EnvironmentBuilder.sln
global.json
Directory.Build.props
Directory.Packages.props
.editorconfig
src/
├── EnvironmentBuilder.Core/
│   ├── Environments/       # snapshots, validation, graph, Compose plan
│   ├── Operations/         # plans, journal protocol, state transitions
│   ├── SeedPacks/          # baseline and initialization rules
│   └── Abstractions/       # genuine I/O boundaries
├── EnvironmentBuilder.Infrastructure/
│   ├── Persistence/        # SQLite + content-addressed files
│   ├── Docker/             # one CLI/Compose process adapter
│   ├── Initialization/     # bounded HTTP requests and safe retry
│   ├── Imports/            # strict JSON and safe ZIP/NDJSON
│   └── Security/           # DPAPI + redaction boundary
└── EnvironmentBuilder.App/
    ├── Composition/
    ├── Shell/
    ├── Features/           # Environments, SeedPacks, Settings, Diagnostics
    ├── Components/         # PageHeader, ContextPanel, StatusBar
    └── Themes/             # Light, Dark, Typography, Components
tests/
├── EnvironmentBuilder.UnitTests/
├── EnvironmentBuilder.IntegrationTests/
└── EnvironmentBuilder.UiTests/
demos/
├── Requests.Api/
├── Scheduler.Api/
├── Reporting.Api/
├── descriptors/
├── seedpacks/
└── fixtures/               # fourth unknown service, malformed imports
scripts/powershell/         # demo fixtures and acceptance runner
```

**Structure Decision**: три производственных assembly имеют разные зависимости: Core проверяется без Windows/WPF/Docker, Infrastructure владеет эффектами, App — presentation и composition. VM тестируются без окна на Windows target. Внутри проектов — cohesive folders; assembly на каждый экран/операцию не нужны. Demo самостоятельны; их имена и маршруты не встроены в менеджер.

## Phase 0 — Research

Разрешены support matrix, Docker integration/cancellation, проверка входов, persistence/recovery, JSON/SeedPack, HTTP retry/secrets, WPF versions/lifetimes, bounded streams и стенд. Решения, rationale, alternatives и первичные источники — [research.md](research.md). Неизвестное фактическое runtime-состояние хранится как `unknown`, а не считается нерешённым проектным вопросом.

## Phase 1 — Design

1. [data-model.md](data-model.md): immutable IDs, snapshot/revision, per-instance dependencies, ledger, generation и journal; recovery без обещания отката.
2. [documents.md](contracts/documents.md) и schemas: точные v1 форматы; semantic validation отдельно от shape.
3. [runtime.md](contracts/runtime.md): preview fingerprint, CLI, ownership, destructive confirmation, init/retry/cancel, observations/retention.
4. [ui.md](contracts/ui.md): navigation, common shell, commands, pending close, темы.
5. [quickstart.md](quickstart.md): команды, fixtures, isolation и SC. Это validation guide будущей реализации.

### Покрытие требований

| Требования | Design / validation boundary |
| --- | --- |
| FR-001–002, FR-006–007, FR-018–020, FR-040 | Snapshot/strict formats, IDs, instance graph, persistence; offline editor/import и failed-write fixtures |
| FR-003, FR-010–011, FR-015–016, FR-024–026, FR-028, FR-036, FR-038 | Runtime compiler/preflight, input delta, Start/Stop, observations, coordinator/journal/cancel/recovery |
| FR-008–009, FR-012–014, FR-027, FR-029, FR-034, FR-037 | ConfirmedQuiescent, exact ownership, candidate revision commit, clear/refresh/delete, isolation/fault matrix |
| FR-017 | Target/image/build, infrastructure image-only, effective defaults и явные updates |
| FR-021–023, FR-030–033, FR-035 | SeedPack immutable import/preview, safe HTTP steps/retries, refs/export/redaction и deferred readiness checks |
| FR-004–005, FR-039 | Shell/routes/list state, loading/empty/error/filter/keyboard states |
| FR-041 | Три независимых .NET demo + RabbitMQ, четвёртый неизвестный сервис и duplicate-instance fixtures |
| QR-001–006, SC-001–013 | Phase 7a: соответствие референсам с приоритетом seed_packs.png; обе темы/DPI/contrast/focus, bounded streams, числовая и пользовательская приёмка по quickstart |

### Последующая реализация

Contracts/strict validation/persistence и fault harness → shell/editor/offline save/preview (US1) → Docker preflight/observations/Start/Stop (US2/3) → journaled edit/clear/refresh (US4/5) → **доработка UI по референсам (Phase 7a, следующий этап)** → delete (US6) → SeedPacks/HTTP initializer/custom components/defaults/import-export/recovery (US7–10). Baseline встраивается в Start до завершения соответствующего slice. Demo и failure/cancel/security tests растут вместе со slice. Новые экраны после Phase 7a используют утверждённые общие компоненты и сравниваются с референсами при реализации. Завершение: полная SC/QR matrix, soak, accessibility и packaging. Все истории обязательны; подробные work items и текущий порядок — в [tasks.md](tasks.md).

### Риски и закрытие

- Abort CLI не доказывает прекращение Docker effect: intent + фактическая сверка; неизвестный HTTP mutation без retry safety не переигрывается.
- SQLite/Docker не имеют общей transaction: revision публикуется после полной cleanup; crash между эффектом и записью ведёт к reconciliation.
- Изменяемые исходники/tags: preview fingerprint, полный preflight до runtime mutation, immutable build snapshot и pinned image ID; delta требует подтверждения.
- Внешний инструмент может скопировать labels: ledger + exact identity + все consumers; совпадение имени не разрешает deletion.
- Большой seed может дать unknown outcome: idempotency contract либо явное решение; journal без request/response bodies.
- WPF проверяется UI smoke, Compose — contract fixtures и capability probe; времена подтверждаются измерениями.

## Complexity Tracking

Нарушений конституции нет; исключения не требуются. Test/demo projects имеют самостоятельные runtime/verification boundaries.

## Planning Validation

Проверить syntax/schema/examples, локальные Markdown links, placeholders, FR/QR/SC coverage и изменённые файлы. Сборка/тесты продукта возможны после реализации. Наблюдённая среда: SDK 10.0.402, runtime 10.0.13, Docker CLI 29.7.2, Compose 5.4.0. Engine недоступен из sandbox (`permission denied` к named pipe), версия сервера неизвестна; CIM/WMI также недоступен. Показатели производительности здесь не объявляются измеренными; доступ к Engine не является условием автоматических проверок.

**Выполнено 2026-10-23**: проверены 5 Draft2020-12 schemas, 6 positive examples, 10 negative shape cases, согласованность ID/graph/baseline в двух environment examples и реальные hashes двух pack entries. Все 78 локальных Markdown links разрешились; unresolved template markers и trailing whitespace отсутствуют. `check-prerequisites.ps1 -Json -PathsOnly` разрешил нужный feature directory; `git diff --check` без ошибок. Повторный cross-artifact review исправил unknown-image preflight, missing-pack deferred checks, quiescent guard, volume incarnation evidence, unsupported one-shot condition, request-path syntax и false-green skipped test gates. До и после planning `.specify/extensions.yml` отсутствует, hooks не зарегистрированы. Изменены только новые planning artifacts; spec/source/constitution сохранены. Application build/tests/containers/UI acceptance не запускались: implementation отсутствует.
