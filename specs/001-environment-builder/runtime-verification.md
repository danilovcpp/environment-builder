# Phase 4 / US2 — реализация и проверка

Дата исходной проверки: 2026-10-23; область проверки уточнена 2026-10-28.

Реализация T051–T076 проверена автоматическими suites; T077 включает UI smoke Inspect. Runtime проверяется с Docker/HTTP doubles, без запуска внешних сервисов. Приложение не ограничивает выпуск или номер сборки Windows.

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

Offline suites включают production SQLite/DPAPI/import/capture boundaries, fake Docker/HTTP
responses и отдельный настоящий child process, который только генерирует stdout/stderr для
проверки concurrent draining, redaction и output bounds. Они не запускают Docker или
сетевые сервисы. UI PNG используют контролируемые fixture values; их CPU/log values не
являются измерениями работающего demo.

Первый финальный unit-прогон выявил гонку CancelRequested/Cancelled. Она исправлена;
добавлена deterministic проверка ожидания terminal commit. Предыдущий failing TRX сохранён
в `artifacts/us2/final/unit/`, актуальный passing прогон — в `unit-fixed/`.

## Текущая область проверки

Автоматические gates: Release build/analyzers, unit, integration, UI и format. Доступность Docker на машине пользователя проверяется capability probe при явной runtime-операции. Полная DPI/keyboard/performance/soak и пользовательская приёмка относятся к последующим задачам.

Архив результатов повторной автоматической проверки 2026-10-27: `artifacts/us2/recheck-2026-10-27/` (restore/build, unit 67/67, integration 137/137, UI 3/3, format).
