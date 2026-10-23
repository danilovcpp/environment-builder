# Phase 0 Research — Environment Builder

**Date**: 2026-10-23 | **Input**: [spec.md](spec.md), [source.md](source.md), [plan.md](plan.md)

Исследование выполнено по трём параллельным направлениям: Docker/Compose, форматы/persistence/security и WPF/toolchain. Ниже — принятые проектные решения; ссылки ведут на первичные источники, проверенные при планировании. Описания поведения внешних технологий отделены от выбранной политики продукта. Все проектные неизвестные разрешены; доступность реального Docker Engine и результаты будущих измерений остаются непроверенными фактами среды.

## 1. Платформа и dependencies

**Decision**: C# 14 / .NET 10 LTS, SDK 10.0.402, Windows 11 x64 обслуживаемых выпусков 24H2+; WPF-UI 4.3.0, CommunityToolkit.Mvvm 8.4.2, MahApps.Metro.IconPacks.Lucide 6.2.1; DI/Sqlite/ProtectedData 10.0.13. Unit: xunit.v3 4.0.2, VS runner 4.0.1 и Test SDK 18.11.0; UI: FlaUI.UIA3 5.0.0. Demo-only RabbitMQ.Client 7.2.2. Точные NuGet/transitive versions фиксируются при restore; publish для win-x64 self-contained folder, без trimming/single-file на первой поставке.

**Rationale**: .NET 10 — LTS до 2028-11-14; .NET 8/9 близки к окончанию поддержки 2026-11-10. WPF UI и Lucide обязательны по конституции, Toolkit закрывает notifications/async commands, три Microsoft packages нужны для DI, SQLite и DPAPI. Текущая .NET matrix не поддерживает произвольную Windows 11 23H2 Home/Pro: support не выводится из одного номера build.

**Alternatives considered**: .NET 8/9; собственный MVVM framework; весь MahApps IconPacks вместо Lucide; GenericHost без hosted services; Windows 10/ARM64 без требования. Не выбраны из-за поддержки, лишней зависимости или объёма.

**Sources**: [.NET lifecycle](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [Windows matrix](https://learn.microsoft.com/en-us/dotnet/core/install/windows), [WPF-UI](https://www.nuget.org/packages/WPF-UI/4.3.0), [Toolkit](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.2), [Lucide](https://www.nuget.org/packages/MahApps.Metro.IconPacks.Lucide/6.2.1), [DI](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection/10.0.13), [xUnit](https://www.nuget.org/packages/xunit.v3/4.0.2), [runner](https://www.nuget.org/packages/xunit.runner.visualstudio/4.0.1), [Test SDK](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.11.0), [FlaUI](https://www.nuget.org/packages/FlaUI.UIA3/5.0.0), [RabbitMQ client](https://www.nuget.org/packages/RabbitMQ.Client/7.2.2).

## 2. Границы и lifetime

**Decision**: Core (`net10.0`), Infrastructure и App (`net10.0-windows`); UI composition root с явными screen/operation lifetimes. Coordinator — app-owned, сериализация изменений по environment ID; OS mutex на data directory не допускает второго writer. Screen cancellation прекращает наблюдение, но не runtime operation. Только UI state marshals на Dispatcher.

**Rationale**: независимые правила и тесты оправдывают три assembly. Interfaces нужны для внешних эффектов, времени и реальных UI interactions. Операция и её журнал должны переживать навигацию. SQLite worker и CPU hashing не занимают UI thread.

**Alternatives considered**: один UI project со всеми side effects; пять обязательных architecture layers; global service locator; operation ownership у страницы. Нарушают testability/lifetime либо добавляют ceremony.

**Sources**: [DI lifetimes/disposal](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection-guidelines), [AsyncRelayCommand](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/asyncrelaycommand), [WPF threading](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model).

## 3. Docker support и adapter

**Decision**: один process adapter с `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, redirected streams, без shell. CLI для Compose и inspect/list/logs/stats/exact-resource mutations. Зафиксировать выбранный локальный npipe endpoint и Engine ID; очистить routing `DOCKER_*`/`COMPOSE_*`, передавать endpoint/file/project явно. Runtime minimum: Engine 28.0.0/API 1.48, Compose 2.24.6; major 2/5 с feature probe. Qualification minimum и current: Desktop 4.96.2, Engine 29.9.1, Compose 5.6.x. Наблюдённый Compose 5.4.0 не отклоняется только из-за major.

**Rationale**: Docker CLI присутствует вместе с Desktop, один adapter избегает второго transport/SDK. JSON inspect/list и документированные stats fields образуют тестируемый контракт. Engine <28 имеет документированное ограничение защиты localhost published ports. Feature probe обязателен, даже когда версия выше minimum.

**Alternatives considered**: Docker.DotNet; собственный Engine HTTP client с npipe framing; v2-only check; remote contexts. Не нужны для данного локального scope. Если CLI output contract меняется, диагностика блокирует affected runtime до qualification, а не угадывает формат.

**Sources**: [CLI/env precedence](https://docs.docker.com/reference/cli/docker/), [contexts](https://docs.docker.com/reference/cli/docker/context/inspect/), [Desktop releases](https://docs.docker.com/desktop/release-notes/), [Compose releases](https://github.com/docker/compose/releases), [port publishing](https://docs.docker.com/engine/network/port-publishing/).

## 4. Preview, изменяемые inputs и Compose

**Decision**: чистый deterministic compiler даёт полный Compose model и preview с placeholders вместо секретов. Token связывает env/revision/canonical effective spec, initializer/pack hashes, secret reference IDs, source tree/Dockerfile/ignore/args/platform и известные image IDs/digests. Offline unknown явно входит в token. Start разрешает ВСЕ inputs до изменения runtime; любой delta, включая unknown→resolved, показывает отдельное подтверждение. Затем использует verified immutable staging build context и pinning image digest/ID, `--no-build --pull never` при `up`.

**Rationale**: config hash Compose не хеширует исходники. Отказ от подтверждения не должен оставлять изменённые containers/volumes/networks. Preview/save не выполняют Dockerfile или registry commands. Проверка registry/build доступна только в явно запрошенной runtime/preflight операции. Внешние загрузки внутри Dockerfile могут быть изменяемыми: фиксируется фактический result image ID без обещания детерминизма сети.

**Alternatives considered**: доверять tag или timestamp; запускать preview через Compose; проверять source только после создания resources. Не обеспечивают FR-003/031.

**Sources**: [Compose config](https://docs.docker.com/reference/cli/docker/compose/config/), [build context](https://docs.docker.com/build/concepts/context/), [registry identity](https://docs.docker.com/reference/cli/docker/buildx/imagetools/inspect/).

## 5. Start, dependencies и cancellation

**Decision**: concrete instance GUID → service key; DAG с условиями running/healthy/HTTP. Генерируется полный Compose, orchestration запускает готовые вершины через detached `up -d --no-deps --no-build --pull never <service>`. Readiness и init — отдельные bounded stages. Никаких abort-on-failure, attached up, auto stop/cleanup при ошибке Start. CLI acceptance mutations — короткие noncancellable stages; cancel останавливает последующее scheduling, ждёт safe point и сверяет результат. Killing клиента не доказывает отмену серверного эффекта.

**Rationale**: attached Compose up при interrupt может остановить контейнеры; это противоречит сохранению работающих компонентов. Временный запуск refresh заранее включён в подтверждённый plan; успех включает подтверждённую остановку. При ошибке/отмене показывается actual state и остатки.

**Alternatives considered**: attached up с CancellationToken; rollback всего окружения; `.Wait()` на UI; cancellation=success. Отклонены из-за semantics и ложного состояния.

**Sources**: [Compose up](https://docs.docker.com/reference/cli/docker/compose/up/), [startup order](https://docs.docker.com/compose/how-tos/startup-order/), [Compose stop](https://docs.docker.com/reference/cli/docker/compose/stop/).

## 6. Ownership и storage isolation

**Decision**: project `eb-<envGuidN>`, service `c-<instanceGuidN>`, managed named volumes и private bridge network. Labels: installation/environment/resource UUID, component UUID где применимо, creation revision и data generation. Resource ledger сохраняет intent до create и actual IDs после; для volume identity — exact name + lifetime UUID/labels. Delete требует совпадения ledger/labels/IDs и отсутствия foreign consumers, включая stopped containers. Bind inputs доступны только read-only и не очищаются.

**Rationale**: project/name недостаточны как доказательство; copied labels и shared mounts требуют блокировки. Только exact resources, без force/prune/down-v/rmi/remove-orphans. Shared images никогда не являются environment-owned data. Повторная проверка непосредственно перед удалением закрывает stale plan; Docker отказ volume-in-use остаётся safe failure.

**Alternatives considered**: произвольные writable host folders; deleting by name prefix; blanket down-v. Увеличивают поверхность удаления за пределы FR-027.

Volume ledger дополнительно фиксирует engine-observed CreatedAt/Driver/Scope/Options: повторное создание со скопированными labels и иным incarnation fingerprint не наследует право удаления. При отсутствии этих evidence deletion блокируется; details — runtime contract. API Docker доверен как локальная authority; доказательство владения не обещает защиты от подмены самой authority.

**Sources**: [labels](https://docs.docker.com/engine/manage-resources/labels/), [Compose volumes](https://docs.docker.com/reference/compose-file/volumes/), [volume inspect fields](https://docs.docker.com/reference/cli/docker/volume/inspect/), [volume removal](https://docs.docker.com/reference/cli/docker/volume/rm/).

## 7. Persistence и crash recovery

**Decision**: одна authoritative SQLite, JSON snapshots и journal/ledger в одной DB; WAL, FULL, foreign keys; dedicated background worker. Native SQLite ≥3.51.3 (либо документированный исправленный backport), проверять `sqlite_version()` и resolved native lock. Pack blobs immutable/content-addressed, promotion до DB references. Intent/result записываются отдельными короткими commits; intent failure запрещает эффект. Старую data generation инвалидировать до первого destructive attempt. Candidate revision публикуется только после verified cleanup. Delete tombstone сохраняется до отсутствия всех scoped ресурсов.

**Rationale**: SQLite транзакция не охватывает Docker/HTTP. Crash after effect before commit даёт unknown, затем reconciliation без automatic destructive replay. Старая спецификация остаётся при partial edit, но прежний init-success не должен сертифицировать частично удалённые данные. Async ADO API SQLite исполняется синхронно; Dispatcher не должен выполнять DB I/O. Native version нельзя вывести из номера managed package.

**Alternatives considered**: JSON files + отдельный несогласованный journal; EF Core на простых snapshots; full event sourcing; best-effort write after effect. Не выбраны из-за consistency или лишней сложности.

**Sources**: [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.13), [SQLite async](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async), [PRAGMA](https://www.sqlite.org/pragma.html), [WAL bug](https://www.sqlite.org/wal.html), [native releases](https://github.com/ericsink/SQLitePCL.raw/releases).

## 8. Versioned formats

**Decision**: UTF-8 JSON, root `kind` + integer `schemaVersion:1`; strict unknown/duplicate property rejection, depth/size bounds. JSON Schema Draft 2020-12 — published tooling contract; runtime использует .NET strict readers и finite semantic validators. Environment embeds immutable component/init snapshots; source/default changes не меняют сохранённое. Format version, definition version, revision и generation независимы.

**Rationale**: формат должен быть переносимым и inspectable, без скриптов и general templating. Runtime validator не должен fetch `$ref`/URLs из imports. Схемы проверяются tooling tests и parity fixtures с runtime reader.

**Alternatives considered**: YAML; arbitrary Compose import; executable plugin; JsonSchema.Net binary dependency. Последняя не нужна для конечного набора DTO, а опубликованная 9.4.1 имеет дополнительные commercial binary terms.

**Sources**: [.NET 10 strict JSON](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries), [JSON Schema 2020-12](https://json-schema.org/draft/2020-12), [JsonSchema.Net binary license](https://www.nuget.org/packages/JsonSchema.Net/9.4.1/License).

## 9. SeedPack import и preview

**Decision**: ZIP содержит только manifest, declared NDJSON sections и initializer JSON. Hash identity — normalized manifest без contentHash + sorted entry path/hash list, независимо от ZIP timestamps/compression. Полная stage validation до library commit, same id/version/hash=no-op, different hash=error. Streaming NDJSON и byte-offset index поддерживают preview без containers. Fixed limits — [documents.md](contracts/documents.md).

**Rationale**: библиотеки ZIP не гарантируют decompressed-size bounds. Reject traversal/rooted/UNC/drive/ADS/reparse/symlink/case-collision/undeclared/executable/nested-archive entries. Archive metadata недостаточно: считать actual decompressed bytes и SHA-256. No external file/HTTP refs. Reference count блокирует удаление используемой версии.

**Alternatives considered**: распаковка без limits; monolithic JSON array на 100 000 records; preview через сервис; редактирование пакетов в приложении. Не соответствуют safety/offline/scope.

**Sources**: [.NET archive safety](https://learn.microsoft.com/en-us/dotnet/standard/io/zip-tar-best-practices).

## 10. HTTP initializer и retry safety

**Decision**: ordered declarative HTTP steps с endpoint ID, relative path, literal/secret-ref headers, inline JSON или streamed SeedPack section, exact statuses/optional JSON-pointer assertions, timeout и retry policy. URLs разрешаются только в endpoint конкретного экземпляра через loopback published mapping; при необходимости Compose добавляет явно показанный auto-assigned loopback control port. No redirects/proxy/cookies/ambient credentials. Persist Started до send, Succeeded после validation; timeout/cancel after dispatch/crash gives Unknown. Safe retry требует declared idempotency semantics либо server-supported stable key; иначе отдельное решение пользователя или refresh.

**Rationale**: один header не доказывает idempotency. Key связывает env/instance/generation/baseline/initializer/step и сохраняется на retry. Exactly-once между независимыми DB/service не обещается. Обычный Start не повторяет durable success той же generation. Response bodies не попадают в diagnostics.

**Alternatives considered**: shell/curl scripts; templating expressions; arbitrary imported absolute URL; blanket retries на timeout; headless browser seeding. Не нужны и не дают safety.

**Sources**: [HTTP retry semantics](https://www.rfc-editor.org/rfc/rfc9110.html), [redirect headers](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.allowautoredirect?view=net-10.0).

## 11. Secrets и redaction

**Decision**: opaque refs + DPAPI CurrentUser blobs; resolve для explicit runtime/observation operation, не для preview/export. Dedicated export DTO. Plaintext не попадает в process arguments/journal. Необходимые runtime files — current-user ACL, deterministic cleanup и orphan-file cleanup при старте. Redaction до memory/UI/disk, включая known values, JSON/URI escaped forms, chunk boundaries и structured sensitive fields. Если secret set нельзя подготовить, вывод соответствующего observation не открывается. Raw HTTP body suppression уменьшает риск неразмеченных секретов.

**Rationale**: DPAPI защищает at rest для Windows identity и не переносит значение на другой компьютер. Redactor должен владеть relevant secret set весь lifetime tail, даже когда Start окончен. Произвольное неизвестное содержимое внешнего процесса не может автоматически получить корректную secret classification; contract требует отмечать sensitive fields, не генерировать unmarked secrets в stdout. SC-011 проверяет все контрольные значения/пути утечки, а не недоказуемое распознавание любого текста.

**Alternatives considered**: plaintext `.env` рядом с export; environment-wide static secrets; redaction только при rendering; serialize DB row then remove known fields. Не обеспечивают отсутствие сохранённых значений.

**Sources**: [ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/10.0.13), [DPAPI scope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope?view=net-10.0), [DPAPI behavior](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata).

## 12. UI и bounded observations

**Decision**: WPF UI resources, затем repo tokens; runtime Light/Dark без пересоздания страниц; OS accent override отключён. Shared shell/tokens/components, recycling virtualization. Один active Inspect session; old session cancel/await/dispose до смены. Logs 10 000 lines/8 MiB per observation, app 32 MiB; line16 KiB; bounded channels/drop counters; metrics300 samples/component @2s; preview100 records/256 KiB page,8 MiB cache. Limits и missing/stale/truncated states видимы.

**Rationale**: виртуализация UI не заменяет streaming imports. QR-003 требует 200% DPI сверх перечисленных в style guide 100/125/150%. `TextTertiary` Light на Canvas даёт около4.39:1: для значимого текста там использовать существующий TextSecondary. Это расчёт токенов, не результат UI QA.

**Alternatives considered**: unbounded ObservableCollection; отдельный tail на каждую посещённую страницу; theme navigation reload; default OS accent вместо фиксированной палитры. Нарушают retention/state/style.

**Sources**: [WPF UI setup](https://wpfui.lepo.co/documentation/getting-started.html), [theme API](https://github.com/lepoco/wpfui/blob/main/src/Wpf.Ui/Appearance/ApplicationThemeManager.cs), [WPF virtualization](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls), [Docker logs](https://docs.docker.com/reference/cli/docker/container/logs/), [Docker stats](https://docs.docker.com/reference/cli/docker/container/stats/).

## 13. Demo и acceptance bench

**Decision**: три .NET10 сервиса Requests/Scheduler/Reporting через RabbitMQ; каждый имеет HTTP health/init/counts и собственный named volume; RabbitMQ только image-source. JSON descriptors/initializers/packs находятся вне app assemblies. Четвёртый неизвестный сервис и два экземпляра одного definition — отдельные fixtures. Reference bench и retention из плана, Release build, warmed images и отдельные cold authoring measurements; benchmark report фиксирует реальные версии/CPU/RAM/VM settings. SC-001/012 проверяются пятью людьми.

**Rationale**: выполняет прямое пожелание source.md и проверяет независимость менеджера от demo domain. 1 000 окружений,100 000 records,100 measurements,20 Stop/Start,10 clear+10 refresh и час logs — воспроизводимые числовые проверки, ещё не результаты.

**Alternatives considered**: встроенные сервисные имена; только mock Docker; заменить пользовательскую приёмку VM tests. Не доказывают FR-041/SC-013/успешность интерфейса.

**Sources**: [RabbitMQ .NET tutorial](https://www.rabbitmq.com/tutorials/tutorial-one-dotnet), [FlaUI](https://github.com/FlaUI/FlaUI).

## Resolution register

| Неизвестное до исследования | Решение |
| --- | --- |
| .NET/UI/test versions и boundaries | §1–2 |
| Docker support/integration/cancellation | §3–5 |
| preview/source/image consistency | §4 |
| ownership/storage/destructive recovery | §6–7 |
| форматы/versions/import compatibility | §8–9 |
| HTTP seeding/unknown outcomes | §10 |
| secret storage/output safety | §11 |
| UI/lifetimes/bounds/bench/demo | §12–13 |

Нерешённых проектных clarifications нет. Engine permission denied, неизвестная редакция/характеристики текущей Windows, неподтверждённые benchmark values и будущие exact image digests отражаются как непроверенная среда и qualification work, без подмены фактов предположениями.
