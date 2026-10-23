# Document Contracts v1

**Status**: проектный контракт, 2026-10-23. [Модель](../data-model.md), [runtime](runtime.md), [UI](ui.md).

## Общие правила

Все документы — UTF-8 JSON с `kind` и `schemaVersion:1`. JSON Schema Draft 2020-12 задаёт форму; таблицы ниже — обязательные semantic rules поверх схем. Root schema IDs `https://schemas.environment-builder.local/v1/…` — логические идентификаторы: resolver связывает их с локальными файлами и никогда не обращается к сети. Импортируемые `$schema`, `$ref`, дополнительные поля или другой `kind` не являются инструкциями валидатору и отклоняются как неизвестные поля. Схемы не загружаются из импорта.

| Документ | Schema | Минимальный пример |
| --- | --- | --- |
| Окружение | [environment.schema.json](environment.schema.json) | [environment.json](examples/environment.json) |
| Библиотечный компонент | [component.schema.json](component.schema.json) | [component.json](examples/component.json) |
| Инициализация | [initialization.schema.json](initialization.schema.json) | [initialization.json](examples/initialization.json) |
| Manifest SeedPack | [seedpack.schema.json](seedpack.schema.json) | [manifest.json](examples/seedpack/manifest.json) |
| Общие типы | [common.schema.json](common.schema.json) | Используются перечисленными схемами |

Примеры показывают форму контракта, а не готовый runnable demo. Реальные demo images, descriptors и большие packs создаются при реализации. При отсутствии проблем каждый пример должен проходить и schema validation, и runtime reader; negative fixtures должны одинаково отклоняться. Runtime не нуждается в отдельной библиотеке JSON Schema.

[Два экземпляра с baseline](examples/environment-two-instances.json) показывают разные instance IDs, настройки/порты, ручную зависимость с точным target ID и назначение пакета только первому экземпляру.

- Duplicate keys, comments, trailing commas, invalid Unicode/UTF-8, depth >32 и неизвестные поля отклоняются. Version проверяется до преобразования/сохранения; миграция неизвестной версии запрещена. Никаких неявных defaults при импорте сохранённого окружения.
- UUID — lowercase canonical `8-4-4-4-12`. Definition/initializer/pack `version` — точная `major.minor.patch[-prerelease]`, без диапазонов и автоматического обновления. `schemaVersion`, `revision`, content version и data generation имеют разный смысл.
- Имя trim-проверяется как непустое; одинаковые отображаемые имена разрешены. `description` необязательно; отсутствие нормализуется в пустую строку при создании typed snapshot.
- Ошибки содержат document path, JSON pointer и безопасное описание, без значения sensitive field. Структурные ошибки блокируют сохранение. Не найденный локальный путь/секрет/образ/pack — readiness issue: описание можно сохранить, Start блокируется.

## Ограничения чтения

| Объект | v1 limit |
| --- | --- |
| Environment JSON | 4 MiB, до100 component instances |
| Component / initializer / manifest JSON | 1 MiB каждый, depth32, до1 000 ordered initializer steps |
| ZIP SeedPack | 512 MiB compressed,2 GiB суммарно expanded,256 entries включая manifest |
| Один section file | 256 MiB expanded |
| NDJSON record | 64 KiB UTF-8 включая line terminator; одна JSON object на строку |
| Preview | до100 records и256 KiB rendered payload/page;8 MiB cache; continuation до конца section |
| HTTP | response ≤1 MiB, timeout1–600s (UI default30s), ≤3 attempts при declared safety |

Превышение import limit — адресная ошибка до library commit, не silent truncation. Preview/render limits не меняют пакет; строка/страница может быть сокращена только с явным признаком и cursor к следующим записям. 100 компонентов — документированный предел v1; тестовый состав20 в плане не является дополнительным ограничением.

## Значения, источники и bindings

`Value` — ровно один из `{ "literal": "text" }` или `{ "secretRef": "secret://<uuid>" }`. Шаблоны `${…}`, expressions и executable callbacks не интерпретируются. `local-path://<key>` — ссылка на mapping текущего пользователя, не путь из пакета. Mapping хранится отдельно; экспорт переносит ref, без host path и содержимого. Missing mapping даёт readiness issue и явный выбор папки пользователем.

`source`:

- `image`: `reference` — image name/tag/digest, без embedded credentials. Auth использует локальный Docker credential context. Запуск фиксирует digest/ID; save/preview не pull/inspect registry.
- `build`: `pathRef`, `contextSubdirectory`, `dockerfile`, `buildArguments` (literal strings), `buildSecrets` (map secret refs). Только для `target`; `infrastructure` принимает лишь image, независимо от сохранённого default. Контекст и Dockerfile должны оставаться внутри разрешённого root; `.` разрешён для context root, `..`, absolute/UNC/drive/ADS escapes и reparse escapes запрещены. Dockerfile/контекст не исполняются до явного Start/refresh. Build secrets передаются через BuildKit secret mounts с приватными файлами, без plaintext args; reference сохраняется, значение нет.

Parameter declaration содержит `name`, `environmentVariable`, `type:string|integer|boolean`, `required`, `secret`, nullable `defaultValue`. Form defaults материализуются в snapshot при создании. Effective `parameters` хранит Value по имени; типизированные literal значения кодируются строками: integer base10 без дроби/переполнения Int64, boolean `true|false`, string как введён. Secret parameter обязан использовать `secretRef`; его фактический тип проверяется после разрешения в runtime. Каждый параметр передаётся в указанную environmentVariable. Два параметра с одним binding или collision с explicit `environment` — structural error. Неизвестный параметр запрещён; необязательный параметр без значения не создаёт переменную.

Ports содержат стабильный `id`, `containerPort`, `protocol`, nullable `hostPort` (engine-assigned при null), `access:loopback|network`, `purpose:application|control`. По умолчанию form создаёт `access:loopback`. Loopback → явный `127.0.0.1`; network → явный `0.0.0.0` после настройки этого конкретного порта. Control всегда loopback/tcp; не добавлять IPv6 all-interfaces неявно. Дубликаты endpoint IDs/host port+protocol и перекрывающиеся binding scopes диагностируются, в том числе между экземплярами. Внешняя занятость порта проверяется отдельно до запуска.

Storage — `managed` (isolated named volume, local driver/local scope без custom options) или `readOnlyInput` (local-path ref), каждый с `id` и абсолютным Linux `targetPath`. Нельзя два mount на один/перекрывающийся target, root `/`, Docker socket/host control mounts или writable external bind. Исходники и входные файлы никогда не становятся deletable owned storage.

Endpoints — `id`, `containerPort`, `scheme:http|https`, `basePath`. Используются readiness/initializer; это адрес компонента, а не imported absolute URL. Readiness: `none`, `running`, `dockerHealth` или `http` с endpoint/path и timeout. `none` не подтверждает готовность и не допускает собственных initializer/baseline applications или зависимости `ready`; для них нужно явно выбрать running/dockerHealth/http. Зависимость healthy требует настоящего Docker health. Runtime при необходимости явно планирует loopback control mapping с auto host port. Platform v1 фиксирована `linux/amd64`, build использует final Dockerfile stage; дополнительных platform/target полей нет.

## ComponentDefinition

`id`+`version` — immutable library key, содержимое — полный snapshot. `componentType`, `contract:{id,version}`, source/defaults, parameter declarations, env/ports/storage/endpoints/readiness, dependency requirements и nullable mandatory `initialization` описывают компонент без executable extension.

`dependencyRequirements` задаёт stable requirement `id`, exact `definitionId`/`definitionVersion`, condition и причину включения. При единственном подходящем экземпляре UI может связать его, при отсутствии — предложить/добавить dependency с видимой причиной; несколько подходящих требуют явного выбора. Missing library definition не разрешается подменой версии. Создание/импорт библиотеки ничего не запускает. Same id/version/canonical content — no-op; different content — conflict, требуется новая version.

Unknown definition names не требуют изменения приложения. Form и JSON import создают один и тот же typed contract. При изменении defaults/library сохранённые environment snapshots не меняются.

## IsolatedEnvironment

`id`, positive `revision`, `name`, optional `description`, `components`, `dependencies`, `baseline`. Экземпляр компонента содержит собственный immutable `id`, полный `definitionSnapshot` и effective `source`, `parameters`, `environment`, `ports`, `storage`. Именно effective fields — вход compiler; default fields snapshot не накладываются поверх них повторно.

- Instance IDs уникальны в окружении, не зависят от definition ID/имени. Два экземпляра одного definition могут иметь разные настройки/данные.
- `dependencies` — единственный resolved graph. Edge: `dependentId`, `requiredId`, nullable `requirementId`, `condition`, `reason`. Non-null requirementId точно связывает requirement из snapshot dependent с требуемым definition/version; каждое обязательное requirement связано ровно один раз. Null — явно добавленная пользователем extra dependency.
- Self-edge, cycle, missing IDs, duplicate bindings, неподдерживаемое condition и неоднозначность запрещены. Error показывает цепочку ID/имён. V1 допускает только running/healthy/ready; one-shot completion condition не поддерживается. Readiness/mandatory init не должны создавать скрытый цикл. Условия запуска уточнены в runtime contract.
- Первое сохранение — новый UUID и revision1 без Docker. No-op save не чистит данные и не увеличивает revision. Любое действительное изменение существующих effective fields, metadata или snapshot проходит destructive edit protocol. Old snapshot действует до verified cleanup и DB commit; ID неизменен.
- Export — отдельный DTO этой схемы, без DPAPI blob/local path mappings/runtime/project labels/journal/observations/data. Secret refs остаются refs. Import всегда создаёт новый env UUID/revision1 и новые instance UUID, переписывая dependencies/baseline targets; existing environment с тем же imported ID не изменяется. Imported revision не продолжается.

## Baseline и applications

`baseline.applications` — упорядоченный список `{componentId, initializer, seedPack}`. `initializer` — полный документ, а `seedPack` — null либо exact `{id,version,contentHash}`. Empty list задаёт пустое начальное состояние, если нет mandatory initializers. Для каждого экземпляра с non-null `definitionSnapshot.initialization` baseline обязан содержать эту точную mandatory snapshot ровно один раз с `seedPack:null` перед его pack applications. UI формирует запись автоматически; несовпадение импортированного snapshot — structural error.

Selected pack требует declared compatible target contract/version, доступный blob точного hash и initializer, который умеет применить выбранные sections. При доступном blob доказанная несовместимость/неизвестная section/неверный target или несовпадение initializer с declared entry — semantic error. При отсутствии pack проверки по его manifest откладываются как readiness issues: переносимое окружение со snapshot можно сохранить, но Start/refresh блокируются до проверки exact blob. Сам initializer snapshot, его target contract и instance reference проверяются всегда. Pack initializer выбирается явно, сохраняется snapshot и при наличии blob совпадает с declared initializer entry по canonical content. Pack не может автоматически заменить mandatory init.

Derived `baselineHash` покрывает effective mandatory initialization и весь ordered application list. `applicationHash` покрывает конкретную application с instance ID, initializer и exact pack ref; exact duplicates запрещены. Hash входит в journal key: одинаковый initializer для двух packs не разделяет success marker. Step success применим только к текущей valid data generation; Stop/Start не меняет generation, reset/edit инвалидирует старую ДО первого destructive attempt. Подробности — data model.

## InitializationDefinition

Root: UUID/version, `targetContract:{contractId,versions[]}`, ordered `steps`. Step: `id`, `title`, `endpointId`, `method`, `path`, `headers`, `body`, `expect`, `timeoutSeconds`, `retry`. Step IDs уникальны в документе; endpointId должен существовать в snapshot target.

- Методы: GET/HEAD/POST/PUT/PATCH/DELETE. `path` — request target с начальным одиночным `/`, относительный к declared endpoint/basePath; запрещены scheme/authority/userinfo/fragment/control characters/traversal, включая decoded traversal. `endpoint.basePath` — `/` либо slash-prefixed path без trailing slash/query/fragment. Итоговый path — basePath без завершающего `/` плюс step.path; `/v1` и `/admin` дают `/v1/admin`. Uri resolution не должен отбрасывать basePath. Нормализованный адрес остаётся в declared endpoint/basePath. GET/HEAD body должен быть `none`.
- Headers — Value map, безопасные HTTP token names; CR/LF, Host/Content-Length/Transfer-Encoding/Connection и routing overrides запрещены. Content-Type для JSON устанавливает interpreter. No redirects, proxy, cookies, ambient Windows credentials или arbitrary remote targets.
- Body — `none`, `inline` JSON value или `seedSection` с sectionId. Section читается stream как JSON array в исходном порядке, без local file access или expressions. Inline payload не может содержать отмеченные secret literals; sensitive header/env values задаются refs.
- `expect.statuses` — точные допустимые codes; optional assertions: JSON pointer и `equals` JSON value. Pointer escape корректен; response ≤1 MiB; status/assertions должны пройти до success. Не сохранять и не показывать raw response/body. Safe error содержит step/target/status, без payload.
- `retry.mode:none` → maxAttempts1. `idempotent` или `key` → maxAttempts1–3 и обязательное `safetyEvidence`, описывающее контракт сервиса; `key` дополнительно задаёт header. Key стабилен для env+instance+generation+baselineHash+applicationHash+initializerHash+stepId. Не использовать retry policy только на основании выбранного HTTP method или наличия header.
- Dispatch после durable Started; отсутствие durable success после возможного эффекта → Unknown. Без declared safety автоматический replay запрещён, UI предлагает явное решение о повторе либо refresh. Успешные шаги текущей generation не повторяются. Previous-step failure/unknown блокирует следующий step и readiness зависимых действий.

## SeedPack layout и identity

ZIP root содержит ровно `manifest.json`, declared `sections/<name>.ndjson`, declared `initializers/<name>.json`. Дополнительные entries/directories не нужны и отклоняются; nested ZIP, executable, links/reparse entries запрещены. Пример: [section](examples/seedpack/sections/requests.ndjson), [initializer](examples/seedpack/initializers/requests.json).

Manifest: UUID/version/title/theme/purpose, compatibility exact contract/version lists, ordered sections с id/title/path/recordCount/bytes/SHA-256, initializers с path/bytes/SHA-256 и contentHash. Section IDs/entry paths уникальны с учётом case-insensitive Windows collisions. Counts/bytes проверяются по actual streaming reads, а не ZIP directory metadata. NDJSON — UTF-8 без blank lines, JSON object на строку, LF либо CRLF; hash считает точные bytes, record count не включает пустую строку после terminal newline. Изменение line endings в payload означает другое содержимое.

Import stages: проверить ZIP bounds/path containment → manifest shape/version → каждую declared entry, actual bytes/hash/count/initializer compatibility → построить byte-offset index → promote immutable blob → DB transaction references. До полного успеха library не меняется. Failed DB commit может оставить unreferenced app-owned staged blob, который безопасно удаляется после проверки отсутствия refs; исходный ZIP не изменяется.

Same id/version/contentHash — повтор без дубля; same id/version с иным hash — ошибка. Нельзя удалить pack version, если на неё ссылаются saved environment или unresolved operation/staged candidate. UI показывает эти ссылки. Новый pack version не заменяет выбранный baseline.

### Hash definition

`canonicalJson` v1: property keys recursively сортируются ordinal по UTF-16, arrays сохраняют порядок; UTF-8 без BOM/whitespace; strings сохраняют Unicode без normalization и используют только обязательные JSON escapes (lowercase `\u` для control chars). Typed integer fields — base10 без leading zeros; boolean/null — JSON literals. Для arbitrary inline JSON body/assertion number tokens сохраняются как в принятом immutable snapshot: `1` и `1.0` консервативно считаются разным содержимым. Это проектный finite serializer, не заявление о полной реализации JCS; общие fixtures фиксируют escaping/property order и numeric tokens.

`contentHash = lowercaseHex(SHA256(canonicalJson(manifest без contentHash)))`. Entry hashes уже входят в manifest, их paths/counts/order также покрыты; ZIP compression/timestamps не влияют. Example manifest имеет реальный hash своих двух payload entries. Definition/initializer/application/baseline hashes используют тот же canonical serializer. Effective spec hash исключает runtime и внутренние DB timestamps; export не содержит derived hashes, кроме immutable pack contentHash.

## Ошибки и совместимость

Unsupported format, corrupt JSON, invalid graph и unsafe archive никогда не создают частичное окружение/pack. Хранилище изолирует ошибочную запись и показывает её отдельно от корректных. Не найденные внешние inputs выводятся полным списком с JSON pointers/instance IDs и без замены settings. Последняя корректная revision защищена transaction; failed write не считается successful save. Recovery, confirmations и retry protocol обязательны даже при shape-valid documents.
