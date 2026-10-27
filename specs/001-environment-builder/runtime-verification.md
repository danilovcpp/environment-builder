# Phase 4 / US2 — реализация и проверка

Дата: 2026-10-23. Область: только Phase 4, T051–T077. Следующие фазы не выполнялись.

**Статус: реализация T051–T076 проверена доступными автоматизированными suites;
обязательная live-приёмка T077 — Blocked. Checkpoint US2 не объявляется пройденным.**

**Актуальное решение (2026-10-27)**: T077 пропущена по прямому указанию пользователя:
обновить Windows невозможно. Задача остаётся `[ ]` с пометкой `SKIPPED`; автоматически
повторять её на текущем хосте не требуется. Следующее выполнение implementation может
перейти к Phase 5. Live-приёмка и полный checkpoint US2 остаются неподтверждёнными.

## Реализованное поведение

- Start принадлежит приложению и повторно использует активный operation ID. Полный preflight
  предшествует изменению runtime: проверяются сохранённая конфигурация, generation, inputs,
  exact packs, секреты, порты, ресурсы и Docker authority.
- Сравнение использует последний действительно показанный Compose preview. Изменение inputs,
  включая unknown → resolved, требует отдельного review; отсутствие preview требует полного
  review. Отказ не запускает image preparation, контейнеры или HTTP initialization.
- Build и read-only inputs копируются в приватный staging с проверкой фактически скопированных
  bytes и гонок источника. Launch использует pinned image IDs, `--no-build --pull never`,
  detached Compose и `--no-deps`. Ledger хранит ресурсные UUID, actual identities,
  Engine ID и volume incarnation; совпадения имён/labels недостаточно для владения.
- HTTP initialization сохраняет Started перед dispatch и результат перед следующим шагом.
  Success key включает instance, generation, baseline/application/initializer hashes и step ID.
  Сохранённый success не повторяется. Safe retry ограничен декларацией, transient outcome,
  числом попыток и общим timeout; неизвестный unsafe результат блокирует продолжение.
- Перед каждым initialization dispatch заново проверяются Engine, container ownership и
  loopback mapping. Redirects, proxy, cookies и ambient credentials отключены; basePath
  сохраняется; body секции передаётся потоком, response ограничен 1 MiB.
- Ошибка/отмена прекращает следующие stages без автоматического Stop/cleanup. После ошибки
  Docker-клиента выполняется read-only observation принятого действия; неизвестный результат
  остаётся доступным для reconciliation.
- Inspect показывает Docker status, отдельный health/readiness, drift, timestamps,
  проверенные mappings, bounded logs и измерения. Missing/stale не заменяются нулём;
  aggregates показывают покрытие. Старый observation session завершается перед новым.
- Закрытие поддерживает dirty guard и Wait/Cancel/Return, сохраняет окно до safe point и
  durable результата. Переходы operation сериализованы; terminal status публикуется после
  commit. Неудача terminal commit блокирует безопасное закрытие.
- Добавлены независимые Requests/Scheduler/Reporting demo с RabbitMQ, отдельными SQLite
  stores, durable outbox/deduplication, descriptors и пакеты на 10/1 000/100 000 записей.
  Прикладные имена и HTTP routes остаются вне manager assemblies.

## Совместимость хранилища

SQLite schema v2 добавляет историю initialization attempts. Миграция v1 → v2 сохраняет
прежние authoritative success-маркеры; отдельная regression проверяет, что они не требуют
нового HTTP dispatch. Версии переносимых JSON-документов остаются v1.

## Выполненная проверка

| Проверка | Фактический результат | Evidence относительно корня репозитория |
| --- | --- | --- |
| Locked restore + NuGet audit, 9 проектов | PASS | Выполнен `dotnet restore EnvironmentBuilder.sln --locked-mode --disable-parallel -m:1 -nr:false` |
| Release build/analyzers | PASS, 0 warnings/errors | `artifacts/us2/final/build.log` |
| Unit/VM | PASS, 67/67, skipped 0 | `artifacts/us2/final/unit-fixed/` |
| Offline integration | PASS, 137/137, skipped 0 | `artifacts/us2/final/offline-fixed/` |
| UI/resources/FlaUI | PASS, 3/3, skipped 0 | `artifacts/us2/final/ui/` |
| Light/Dark Inspect и общие экраны | PASS, PNG просмотрены, binding/resource errors отсутствуют | `artifacts/us2/final/ui-png/` |
| `dotnet format --verify-no-changes --no-restore` | PASS | Выполнен после последнего изменения C# |
| Live Docker | BLOCKED, 1 failed prerequisite, 0 passed/skipped | `artifacts/us2/final/docker/` |

Offline suites включают production SQLite/DPAPI/import/capture boundaries, fake Docker/HTTP
responses и отдельный настоящий child process, который только генерирует stdout/stderr для
проверки concurrent draining, redaction и output bounds. Они не запускают live Docker или
сетевые сервисы. UI PNG используют контролируемые fixture values; их CPU/log values не
являются измерениями работающего demo.

Первый финальный unit-прогон выявил гонку CancelRequested/Cancelled. Она исправлена;
добавлена deterministic проверка ожидания terminal commit. Предыдущий failing TRX сохранён
в `artifacts/us2/final/unit/`, актуальный passing прогон — в `unit-fixed/`.

## Блокировка T077 и границы подтверждения

Read-only диагностика обнаружила Windows `10.0.22631.0` (23H2), Docker Desktop 4.88.1,
Engine 29.7.2/API 1.55 и Compose 5.4.0; Engine доступен, Linux/amd64 на WSL2.
Production probe требует Windows 11 x64 24H2+ (`build >= 26100`) и возвращает
`windows-24h2-x64-required` до runtime effects. Gate не ослаблялся.

Opt-in test подготовлен для двух успешных независимых окружений и двух отдельных failure
fixtures: source/image delta refusal, duplicate Start, baseline/business exchange,
readiness/init failure с сохранением running containers, logs/metrics и exact cleanup.
Все ID выдаются isolated store и входят в manifest. Cleanup использует journal,
Engine pinning, production ownership/consumer verifier, graceful stop и exact removal
без force/prune/image deletion. При блокировке cleanup store и evidence сохраняются.

Эти live-сценарии на текущем хосте **не выполнялись после prerequisite**. Не подтверждены
реальные demo build/run/exchange, Docker mappings/logs/metrics, сохранение running containers
при реальной ошибке, а также minimum/current qualification matrices. T077 оставлена `[ ]`.
Полная DPI/keyboard/performance/soak и пользовательская release-приёмка здесь также не заявляются.

После обновления Windows, из корня репозитория:

```powershell
$env:ENVIRONMENTBUILDER_DOCKER_TESTS = '1'
$env:ENVIRONMENTBUILDER_RUNTIME_ARTIFACTS = Join-Path $PWD 'artifacts/us2/requalification/live'
dotnet test tests/EnvironmentBuilder.IntegrationTests/EnvironmentBuilder.IntegrationTests.csproj -c Release --no-build --no-restore --filter 'Category=Docker' --logger trx --results-directory artifacts/us2/requalification/docker
```

Перед повтором требуется актуальная Release-сборка. Отсутствие prerequisite снова должно
давать Blocked/failure, а не successful skip. Остатки из `cleanup-blocked.json` требуют
отдельной сверки; глобальная очистка не допускается.

## Workflow

Чеклист requirements.md: 16/16, read-only. `.specify/extensions.yml` отсутствует;
before_implement/after_implement hooks не зарегистрированы. Git-ветка не переключалась,
commit/publish не выполнялись. T051–T076 отмечены `[X]`; T077 и задачи следующих фаз сохранены
незавершёнными. Изменения ограничены US2 и необходимыми shared boundaries/regressions.

## Повторная проверка — 2026-10-27

Область повторного выполнения: следующая незавершённая Phase 4, только оставшаяся T077.
Production-код и тесты не изменялись; Phase 5 и последующие фазы не выполнялись.
Пользователь явно попросил пропустить T077, поскольку обновить Windows невозможно.
В tasks.md сохранён `[ ]` с пометкой `SKIPPED`, а не `[X]`: live-сценарии не подтверждены.

Evidence: `artifacts/us2/recheck-2026-10-27/` относительно корня репозитория.

| Проверка | Фактический результат | Evidence |
| --- | --- | --- |
| Locked restore, 9 проектов | PASS | `restore.log` |
| Release build/analyzers с `-warnaserror` | PASS, 0 warnings/errors | `build.log` |
| Unit/VM | PASS, 67/67, skipped 0 | `unit/`, `unit.log` |
| Offline integration вне песочницы | PASS, 137/137, skipped 0 | `offline-unrestricted/`, `offline-unrestricted.log` |
| UI/resources/FlaUI вне песочницы | PASS, 3/3, skipped 0 | `ui/`, `ui.log`, `ui-png/` |
| `dotnet format --verify-no-changes --no-restore` вне песочницы | PASS, exit 0 | `format-unrestricted.log` |
| Opt-in live Docker вне песочницы | BLOCKED до runtime effects: 1 failed, 0 passed/skipped | `docker-unrestricted/`, `docker-unrestricted.log` |

В первом прогоне песочница запретила установку ACL приватных тестовых каталогов:
offline integration 87 passed / 50 failed, Docker 1 failed до проверки платформы.
Также песочница запретила служебный named pipe Roslyn при проверке форматирования.
Исходные логи/TRX сохранены в `offline/`, `docker/`, `offline.log`, `docker.log`,
`format.log`; повтор соответствующих команд вне песочницы разрешил эти ограничения
без изменения кода или проверок безопасности.

Повторный live-тест вернул `DockerTestBlockedException` с кодом
`windows-24h2-x64-required`. В `host.json` зафиксированы Windows `10.0.22631`,
архитектура X64 и отрицательная проверка build >= 26100. Read-only Docker version
из песочницы дал permission denied к named pipe; текущая server version этим запуском
не подтверждена. CLI 29.7.2 и Compose 5.4.0 доступны.

Статус T077 после решения пользователя — **SKIPPED**, фактический runtime gate —
**BLOCKED**. Реальные demo build/run/exchange, mappings/logs/metrics, failure isolation
и minimum/current qualification в этом повторе не подтверждены. UI smoke 3/3
не заменяет Docker-приёмку. Новых задач для отметки `[X]` нет; прежние отметки сохранены.
Для последующего implementation следующая фаза — Phase 5; T077 автоматически
на этом хосте не повторять. `.specify/extensions.yml` отсутствует, hooks не зарегистрированы.
