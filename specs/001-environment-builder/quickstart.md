# Quickstart и acceptance validation

**Status**: руководство для будущей реализации по [plan.md](plan.md). На этапе `$speckit-plan` исходники, solution, tests, demo и scripts ниже **ещё не существуют**. Команды задают требуемый воспроизводимый интерфейс будущих artifacts, не являются отчётом об их выполнении. Планирование не создаёт `tasks.md` и не реализует приложение.

Контракты: [documents](contracts/documents.md), [runtime](contracts/runtime.md), [UI](contracts/ui.md), [data model](data-model.md). Все SC-001–SC-013 входят в приёмку, включая ручные пользовательские измерения.

## 1. Предпосылки и reference bench

- Обслуживаемая Windows 11 x64 24H2+, PowerShell 7, .NET SDK 10.0.402 с Desktop targeting pack. SDK/package versions фиксируются `global.json`, central package versions и lock files.
- Для Docker сценариев: local Docker Desktop/WSL2 в режиме Linux containers; Engine ≥28.0.0/API ≥1.48; Compose ≥2.24.6, major 2 или 5 только после capability probe. Отдельно квалифицируются minimum и актуальная матрицы из плана; высокий version number сам не доказывает поддержку.
- Предлагаемый reference bench: ≥8 logical CPUs, 32 GiB RAM, SSD с ≥30 GiB свободного места; Docker VM 4 CPUs/8 GiB. Это проектное условие, **не согласованная с пользователем или уже измеренная характеристика текущего ПК**. Перед замерами записываются фактические CPU/RAM/disk/OS edition/build, Docker VM limits и версии.
- Для UI/FlaUI требуется интерактивный unlocked desktop. DPI 200% при окне 1280×720 DIP требует физический viewport минимум 2560×1440. Headless CI не подтверждает UI gates.
- Docker acceptance использует отдельный data directory и уникальные test environment/resource IDs. Ни один тест не выполняет global prune, blanket `down -v`, удаление shared images, произвольных host folders или пользовательских окружений.

Текущая среда планирования: SDK 10.0.402/runtime 10.0.13, Docker CLI 29.7.2, Compose 5.4.0. Доступ к Engine named pipe завершился `permission denied`; server/API/version/state неизвестны. CIM/WMI также недоступен. CPU/RAM/OS edition и performance bench здесь не подтверждены. Это ограничения текущей проверки, а не исправимые приложением факты или passed gates.

## 2. Workspace и изоляция

Следующие PowerShell команды выполняются из репозитория после реализации; абсолютные filesystem paths вычисляются явно:

```powershell
$repoPath = 'D:\Space\Personal\environment-builder'
Set-Location -LiteralPath $repoPath
$runId = [guid]::NewGuid().ToString('N')
$runRoot = Join-Path $repoPath "artifacts\acceptance\$runId"
$fixtureRoot = Join-Path $runRoot 'fixtures'
$dataRoot = Join-Path $runRoot 'data'
$reportRoot = Join-Path $runRoot 'reports'
New-Item -ItemType Directory -Path $runRoot, $dataRoot, $reportRoot | Out-Null
$env:ENVIRONMENTBUILDER_DATA_DIR = $dataRoot
$env:ENVIRONMENTBUILDER_DOCKER_TESTS = '0'
```

`ENVIRONMENTBUILDER_DATA_DIR` задаёт root SQLite/blobs/private runtime files приложения и harness; это override location, а не способ ослабить validation или ownership. Приложение допускает одного writer на resolved data directory. Integration opt-in `ENVIRONMENTBUILDER_DOCKER_TESTS=1` сам не разрешает доступ к чужим ID; fixture allowlist и runtime ownership остаются обязательны. Tests не читают обычный `%LOCALAPPDATA%/EnvironmentBuilder`.

UI, acceptance runner и приложение по очереди открывают один и тот же data directory. Не запускать mutating harness параллельно с приложением. Каждый повтор сценария создаёт отдельный child data directory и fixture IDs, поэтому сценарии не требуют прохождения предыдущих через UI.

## 3. Требуемые interfaces scripts

Эти scripts должны появиться в `scripts/powershell/` при реализации. Они принимают абсолютные пути, возвращают exit code 0 при успехе и ненулевой при ошибке; skipped/manual/unverified не записываются как passed. Изменения fixture store завершаются до запуска приложения.

| Script / параметры | Назначение |
| --- | --- |
| `Get-AcceptanceBaseline.ps1 -ArtifactsDirectory <path> [-DockerVmCpu <int> -DockerVmMemoryGiB <int>]` | Записать `baseline.json`: SDK, OS, CPU/RAM/disk, Docker CLI/Compose, context/endpoint, Engine/API/OSType, capabilities и ошибки probes. VM arguments — заявленные оператором значения, отдельно от измеренных; отсутствие доступа сохраняется как unknown |
| `New-AcceptanceFixtures.ps1 -OutputDirectory <new-path> -EnvironmentCount <int> -SeedRecordCount <int>` | Детерминированно создать declarative fixtures, ожидаемые hashes/counts, малый/средний/100k-record пакеты и manifest; не создавать runtime resources и не требовать UI/Docker. Existing nonempty output отклонить |
| `Initialize-AcceptanceStore.ps1 -DataDirectory <path> -FixtureDirectory <path>` | Инициализировать отдельный store через production importer/persistence boundary, без UI/process/HTTP/Docker effects; записать ID mapping; nonempty несогласованный store отклонить |
| `Invoke-Acceptance.ps1 -Scenario <SC-001..SC-013> -DataDirectory <path> -FixtureDirectory <path> -ArtifactsDirectory <path> [-EnableDocker]` | Подготовить независимое состояние сценария, проверить isolation/opt-in, выполнить automated portion и записать JSON report + timings. SC-001/SC-012 создают формы и evidence для пяти участников; human outcomes вносятся отдельно |
| `Assert-ValidationResults.ps1 -ResultsDirectory <path> -RequiredSuite <Unit,OfflineIntegration,DockerIntegration,UI>` | Проверить TRX и reports: каждая обязательная suite реально выполнила tests, prerequisites доступны, нет failure/skipped обязательных cases; вывести ненулевой exit при отсутствующей suite/нуле исполненных tests/blocked gate |

Статусы runner: `Passed`, `Failed`, `Blocked`, `Unverified`, `ManualPending`; exit0 разрешён только для полностью `Passed` выбранного сценария. Отсутствие Engine/desktop, manual evidence или участников не считается успешным выполнением. В opted-in Docker/UI suites недоступная обязательная prerequisite даёт failure/blocked, не silently skipped. Нулевое число matched/executed tests или all-skipped TRX не проходит `Assert-ValidationResults`, даже если сам `dotnet test` вернул0. SC-001/012 остаются ManualPending с ненулевым exit до внесения результатов пяти участников; automated portion не подменяет целый сценарий.

`-EnableDocker` требует одновременно env opt-in `1`, успешную matrix/capability проверку и явный manifest test IDs. Runner до runtime effects перечисляет принадлежащие тесту planned resources. Fault injection разрешена только в test harness с этими ID, не импортируемыми локальными script commands. Cleanup runner использует production plan/ownership checks; сохраняет report/остатки при отказе, не делает best-effort global cleanup.

```powershell
& (Join-Path $repoPath 'scripts\powershell\Get-AcceptanceBaseline.ps1') -ArtifactsDirectory $reportRoot -DockerVmCpu 4 -DockerVmMemoryGiB 8
& (Join-Path $repoPath 'scripts\powershell\New-AcceptanceFixtures.ps1') -OutputDirectory $fixtureRoot -EnvironmentCount 1000 -SeedRecordCount 100000
& (Join-Path $repoPath 'scripts\powershell\Initialize-AcceptanceStore.ps1') -DataDirectory $dataRoot -FixtureDirectory $fixtureRoot
```

Baseline script записывает данные также при Engine unavailable, но runtime qualification остаётся blocked/unverified. Read-only `docker version --format '{{json .}}'`, `docker compose version --format json` и capability probes не подменяются одной проверкой CLI version.

## 4. Restore, build, tests, formatting и publish

После реализации lock files коммитятся с разрешёнными transitive dependencies. Отдельно записываются `sqlite_version()` и native SQLite package version; только managed package number не доказывает выполнение требования исправленного SQLite из research.

```powershell
dotnet --info
dotnet restore (Join-Path $repoPath 'EnvironmentBuilder.sln') --locked-mode
dotnet build (Join-Path $repoPath 'EnvironmentBuilder.sln') --configuration Release --no-restore -warnaserror
dotnet test (Join-Path $repoPath 'tests\EnvironmentBuilder.UnitTests\EnvironmentBuilder.UnitTests.csproj') --configuration Release --no-build --no-restore --logger trx --results-directory (Join-Path $reportRoot 'unit')
dotnet test (Join-Path $repoPath 'tests\EnvironmentBuilder.IntegrationTests\EnvironmentBuilder.IntegrationTests.csproj') --configuration Release --no-build --no-restore --filter 'Category!=Docker' --logger trx --results-directory (Join-Path $reportRoot 'integration-offline')
dotnet format (Join-Path $repoPath 'EnvironmentBuilder.sln') --verify-no-changes --no-restore
```

Unit tests проверяют Core и ViewModels без окна, live Docker, сети и arbitrary sleeps. Offline integration проверяет storage/import/schema parity/process/HTTP doubles и failure boundaries. Fixtures валидируются published schemas и тем же strict runtime reader; malformed/duplicate fields, semantic cycles и rejected formats должны иметь negative fixtures.

```powershell
$env:ENVIRONMENTBUILDER_DOCKER_TESTS = '1'
dotnet test (Join-Path $repoPath 'tests\EnvironmentBuilder.IntegrationTests\EnvironmentBuilder.IntegrationTests.csproj') --configuration Release --no-build --no-restore --filter 'Category=Docker' --logger trx --results-directory (Join-Path $reportRoot 'integration-docker')
dotnet test (Join-Path $repoPath 'tests\EnvironmentBuilder.UiTests\EnvironmentBuilder.UiTests.csproj') --configuration Release --no-build --no-restore --filter 'Category=UI' --logger trx --results-directory (Join-Path $reportRoot 'ui')
& (Join-Path $repoPath 'scripts\powershell\Assert-ValidationResults.ps1') -ResultsDirectory $reportRoot -RequiredSuite Unit,OfflineIntegration,DockerIntegration,UI
dotnet publish (Join-Path $repoPath 'src\EnvironmentBuilder.App\EnvironmentBuilder.App.csproj') --configuration Release --runtime win-x64 --self-contained true -p:RestoreLockedMode=true -p:PublishTrimmed=false -p:PublishSingleFile=false --output (Join-Path $runRoot 'publish\win-x64')
```

Publish использует lock для предусмотренного `win-x64` restore graph и выдаёт self-contained folder. Отдельный smoke выполняется из опубликованной папки без SDK dependency: startup/offline description/preview/themes и затем runtime preflight. Отсутствие Docker или interactive desktop отражается как соответствующий непройденный gate, а не успешный skip. Tests/project filters `Category=Docker` и `Category=UI` — обязательная convention будущего harness.

Ручной запуск после build использует тот же изолированный data root; перед следующим mutating test приложение закрывается с безопасным завершением операций:

```powershell
dotnet run --project (Join-Path $repoPath 'src\EnvironmentBuilder.App\EnvironmentBuilder.App.csproj') --configuration Release --no-build
& (Join-Path $runRoot 'publish\win-x64\EnvironmentBuilder.App.exe')
```

Команды запуска выполняются последовательно после закрытия предыдущего окна. Второй одновременно запущенный writer с тем же data root должен получить понятное сообщение о занятом хранилище.

## 5. Fixtures и demo

`demos/Requests.Api`, `demos/Scheduler.Api`, `demos/Reporting.Api` — три независимых .NET 10 сервиса, взаимодействующих через RabbitMQ; у каждого HTTP readiness/init/counts, собственное persisted storage и deterministic probe данных. RabbitMQ задаётся image-only infrastructure. Build contexts, descriptors, initialization documents и SeedPacks лежат в demo, вне app assemblies; образы фиксируются конкретными resolved IDs/digests для измерения warmed runtime. Pull/build latency записывается отдельно от UI latency.

Fixture generator создаёт:

- Окружение с этими тремя targets + broker; две независимые копии, контрольные пользовательские данные и посторонний ресурс с похожим именем. Foreign fixture создаётся harness и никогда не включается в cleanup manifest выбранного окружения.
- Четвёртый неизвестный сервис через generic fixture definition; два экземпляра одного definition с разными ID/settings/storage и instance-addressed dependencies/baseline.
- Малый/средний/100 000-record SeedPacks с expected entities/counts и content hashes; одинаковая identity/version/hash, конфликтующее содержимое и неподдерживаемая версия.
- Negative imports: path traversal, external/absolute refs, executable entry, oversized record/archive, missing section, duplicate property, invalid dependency и отсутствующий apply contract.
- Fault points до/после каждого mutating этапа, Docker disconnect/unknown и restart markers; lost HTTP response для безопасного/небезопасного retry; source/image drift после preview.
- Контрольные секреты во всех supported channels: settings, escaped process output/chunk boundaries, initializer response и errors; sentinel никогда не используется как реальный credential.

Форма/импорт и preview используют эти же внешние documents. Имена Requests/Scheduler/Reporting и маршруты demo не получают специальных веток в менеджере.

## 6. Запуск приёмки и ожидаемые доказательства

Пример offline сценария и explicit Docker сценария (каждый runner создаёт независимый child store):

```powershell
& (Join-Path $repoPath 'scripts\powershell\Invoke-Acceptance.ps1') -Scenario 'SC-009' -DataDirectory $dataRoot -FixtureDirectory $fixtureRoot -ArtifactsDirectory $reportRoot
$env:ENVIRONMENTBUILDER_DOCKER_TESTS = '1'
& (Join-Path $repoPath 'scripts\powershell\Invoke-Acceptance.ps1') -Scenario 'SC-004' -DataDirectory $dataRoot -FixtureDirectory $fixtureRoot -ArtifactsDirectory $reportRoot -EnableDocker
```

Все сценарии ниже используют точные определения из [spec.md](spec.md). Report содержит run ID, actual versions/bench, fixture hashes, environment/operation IDs, monotonic timings, thresholds, expected/actual result, outcome и evidence paths. Секреты и raw HTTP bodies в report не сохраняются. Формальные JSON/behavior contracts берутся из contracts, а не дублируются implementation code в этом руководстве.

| SC | Независимая процедура / ожидаемый результат |
| --- | --- |
| **SC-001** | Пять представителей аудитории получают чистый offline store и descriptors; ≥4/5 за ≤10 min создают и сохраняют 3 targets + infrastructure, находят preview и верно указывают отсутствие containers. Время, наблюдение и ответ записываются человеком; automation подтверждает zero runtime effects |
| **SC-002** | Открыть Release UI со store из 1 000 описаний, включая long names/errors; список доступен ≤3 s. На 100 последовательностях search/filter ≥95 результатов ≤1 s; cold opening и warmed interactions записать раздельно |
| **SC-003** | В 100 измерениях ≥95 видимых acknowledgements команды ≤1 s. Во время 10-minute operation переход и возврат к progress ≤1 s/action; operation ID сохраняется, duplicate click не создаёт новую работу |
| **SC-004** | Подготовить и изменить demo business data; 20 Stop/Start. Все sentinel данные сохранены, successful initialization той же generation повторилась 0 раз; stopped подтверждён фактически |
| **SC-005** | На stopped fixture проверить name-only и состав/settings edits, cancel, no-op Save, успешную и partial cleanup. Отдельно saved-never-started fixture с verified empty resource set допускает Edit через ConfirmedQuiescent, сохраняя display NeverStarted. Cancel сохраняет data/revision, успех удаляет прежние данные и увеличивает revision ровно на 1 при прежнем ID; partial не публикует candidate revision |
| **SC-006** | 10 независимых clear и 10 refresh. Clear удаляет прежние данные/marks без изменения revision/baseline; refresh восстанавливает expected baseline counts и stopped state. Проверить empty baseline, temporary runtime stages и failure before deletion/во время применения; hidden leftovers/duplicates отсутствуют |
| **SC-007** | Две работающие demo-копии + foreign fixture. Для edit/clear/refresh выбрать и подтвердить stopped одной копии; для delete проверить stop plan. Контрольные данные второй/foreign неизменны. Forged labels, externally recreated volume с copied labels и другим CreatedAt/driver/scope fingerprint, shared volume consumer и stale confirmation блокируют affected delete; отсутствующий incarnation evidence также блокирует |
| **SC-008** | Fault matrix: disconnect, forced restart до/после каждого mutating stage; ambiguous initializer; partial Start; cancel и обычный close. Изменить source/image после preview: delta требует нового подтверждения, отказ даёт zero runtime/data effects. Видны actual/unknown, steps/остатки без ложного stopped/success/rollback. Start failure сохраняет работающие компоненты. Close ждёт completion/safe cancellation+persistence; noncancellable stage имеет объяснение |
| **SC-009** | При выключенном/недоступном Docker открыть 100 000-record SeedPack: первая ограниченная страница ≤2 s; 100 records/256 KiB maximum, продолжение доступно, bounded parse/cache. Просмотр/импорт даёт 0 process/container/init HTTP effects |
| **SC-010** | 60 min поток ≥100 log lines/s и 100 environment switches. На active observation ≤10 000 lines/8 MiB, приложение ≤32 MiB retained logs; line≤16 KiB. Метрики ≤300 samples/component @2s; output не смешивается, subscriptions возвращаются к baseline, navigation проходит SC-003. Dropped/truncated counts видны |
| **SC-011** | Все sentinel secrets отсутствуют в preview/export/UI logs/errors/diagnostics и persisted reports/logs; проверить escaped/chunked values и response paths. Import/preview даёт 0 runtime effects. С отдельного LAN компьютера default ports недоступны; явное network access открывает только выбранный port. Без второй машины network часть остаётся unverified |
| **SC-012** | Пять представителей аудитории: ≥4/5 объясняют различия observation/clear/refresh и за ≤2 min находят prepared component failure. Дополнительно выполнить keyboard/theme/DPI matrix ниже; автоматические UI результаты не заменяют человеческие объяснения |
| **SC-013** | Полный demo cycle: create/start/message exchange/seed/Stop-Start/refresh/delete. Четвёртый неизвестный service импортируется без code changes; два instance одного definition сохраняют отдельные settings/data/dependencies, init success одного не засчитывается другому |

## 7. UI acceptance matrix

На каждой комбинации **Light/Dark × 100/125/150/200% DPI** проверить окно от 1280×720 DIP: list, create/edit, Inspect, context Compose, SeedPacks, Settings, Diagnostics, dirty/destructive/input-delta/close dialogs. Изменение масштаба выполняет оператор/Windows test agent; test report фиксирует фактический DPI, а не только requested value.

- Только клавиатурой пройти navigation/inputs/table actions/preview pages/dialog choices; focus visible, доступные имена icon commands и читаемые disabled reasons. Escape dirty dialog оставляет draft; dialog закрытие возвращает focus к инициатору.
- Long names, empty/large/error/unknown/Docker-unavailable states, no healthcheck, measured zero/stale/missing и partial aggregate не перекрывают commands. ContextPanel можно свернуть; scroll сохраняет доступ к обязательным полям.
- Во время draft и активной operation сменить тему: route/layout/draft/selection/scroll/progress не теряются; ViewModel/operation identity сохраняется. Проверить Hover/Pressed/Selected/Focused/Disabled/error states.
- Проверить contrast на фактическом фоне: ordinary meaningful text ≥4.5:1, large text/key graphics ≥3:1. Отдельная известная пара Light TextTertiary/Canvas требует TextSecondary для значимого текста. Palette HEX допустимы только в resource definitions.
- Снять screenshots обеих тем, сохранить automation tree/focus evidence и binding/resource diagnostics. Любая новая binding error — failure; отсутствие interactive run — unverified.

## 8. Завершение и handoff

Ожидаемая поставка: успешные restore/build/tests/format, опубликованная win-x64 папка, schema/fixture reports, current+minimum Docker qualification, SC reports, crash/security/isolation evidence, UI screenshots/keyboard/DPI/contrast и результаты 5-person приёмки. Performance report хранит сырые 100 измерений и percentile/count, а не только среднее.

После теста runner показывает оставшиеся test-owned resources и выполняет только подтверждённый scoped cleanup; при failure данные диагностики и tombstone сохраняются. Наличие непроверенной среды, blocked Engine доступа, отсутствующего LAN probe/interactive desktop/participants или непрошедшего threshold явно перечисляется в handoff. Никакая часть этой инструкции не является заявлением о выполнении будущих тестов на этапе планирования.
