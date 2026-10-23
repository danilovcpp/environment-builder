# Data Model — Environment Builder

**Feature**: [spec.md](spec.md) | **Design**: [plan.md](plan.md), [research.md](research.md)

Модель разделяет сохранённое описание, фактическое наблюдение и журнал внешних эффектов. Названия и обязательность полей переносимых документов определены в [documents.md](contracts/documents.md) и JSON schemas; ниже заданы связи, инварианты, локальные записи и границы транзакций. Это дизайн будущей реализации, а не существующая схема базы или результат runtime-проверок.

## 1. Идентичности и версии

| Значение | Назначение и область |
| --- | --- |
| `installationId` | Локальный UUID установки/data directory; участвует в доказательстве владения ресурсами, не экспортируется |
| `environmentId` / `IsolatedEnvironment.id` | Неизменяемый UUID окружения, независимый от имени и Compose project |
| `componentId` / `EnvironmentComponent.id` | Неизменяемый UUID экземпляра внутри окружения; все зависимости и назначения baseline адресуются ему |
| `schemaVersion` | Целая версия внешнего формата; v1 принимается явно, другая версия не преобразуется предположительно |
| `revision` | Положительный последовательный номер сохранённой конфигурации одного окружения; начинается с 1 |
| `definitionId`, `definitionVersion`, `definitionHash` | UUID и semver неизменяемой версии библиотечного описания, hash её содержимого; эффективное содержимое также находится в snapshot окружения |
| `packId`, `packVersion`, `contentHash` | Точная неизменяемая версия SeedPack; одинаковая идентичность/версия с другим содержимым отклоняется |
| `baselineHash` | Производный SHA-256 нормализованного эффективного baseline, включая назначения экземплярам и точные initializer/package hashes |
| `applicationHash` | Производный SHA-256 одного baseline application; различает применение одного initializer к разным пакетам в одном экземпляре |
| `dataGenerationId` | UUID поколения runtime-данных; не равен revision или baselineHash |
| `resourceId` | UUID конкретного жизненного цикла управляемого ресурса; повторное создание под тем же именем не сохраняет идентичность |
| `operationId`, `stepId` | UUID операции и устойчивый ID её этапа; переживают навигацию и перезапуск процесса |

Локальные времена записываются как UTC instant; UI применяет локальную зону. Canonical JSON рекурсивно сортирует имена свойств UTF-16 ordinal, сохраняет порядок массивов, кодирует UTF-8 без незначащих пробелов с минимальным JSON escaping; целые поля формата — base 10. Для произвольного inline JSON сохраняется лексема числового токена: различие представления консервативно считается изменением. Общий JCS numeric engine не вводится. Служебные timestamps, observation, draft UI и runtime-результаты не создают конфигурационных изменений. `schemaVersion`, версия библиотечного документа, revision и generation изменяются независимо.

## 2. Сохранённые окружения и компоненты

### IsolatedEnvironment и EnvironmentRevision

`IsolatedEnvironment` содержит `kind`, `schemaVersion`, `id`, `revision`, имя, описание, `components`, `dependencies`, `baseline`. Локальная запись добавляет время создания/сохранения, canonical configuration hash, индексные поля списка и ссылки на текущее поколение и возможную незавершённую операцию. Runtime status и измерения не являются полями экспортируемого описания.

`EnvironmentRevision` — текущий успешно опубликованный snapshot конфигурации и его номер. Архив всех прежних документов и возврат данных к прежней ревизии не предусматриваются. Во время редактирования прежний snapshot остаётся текущим, а кандидат хранится отдельно в операции; он не объявляется новой ревизией до завершения очистки.

Инварианты сохранения:

- Непустое после trim имя и минимум один компонент; отображаемые имена могут совпадать.
- ID окружения и существующих экземпляров нельзя менять через редактор. Новый экземпляр получает новый ID даже при одинаковом definition.
- Структурные ошибки блокируют save: неверный тип/версия документа, повтор ID, некорректное значение параметра, неоднозначная/отсутствующая зависимость, цикл, конфликт портов в конфигурации, неверная baseline-ссылка.
- Недоступный локальный путь, образ, секрет или пакет создаёт `ReadinessIssue`; корректное описание сохраняется, зависимая runtime-команда недоступна. Значения не заменяются defaults.
- Создание сохраняет revision 1 без Docker, сборки и наполнения. Импорт всегда создаёт независимое окружение с новым ID и revision 1; внутренние instance IDs и адресующие их ссылки согласованно переназначаются.
- Любое изменение сохраняемых пользовательских полей существующего окружения, включая одно имя, требует подтверждённой остановки, отсутствия конфликтующей операции и подтверждения очистки. Новая ревизия ровно `previous + 1` публикуется после verified cleanup.
- Save без изменения canonical configuration — no-op: номер не увеличивается, очистка не выполняется.
- Guard `ConfirmedQuiescent` допускает также verified empty resource set никогда не запускавшегося окружения. Он означает фактически проверенное отсутствие running/restarting/paused/unknown owned containers и conflict, отдельно от display NeverStarted/Stopped. Docker unavailable не подтверждает guard.

### EnvironmentComponent

Экземпляр содержит `id`, полный `definitionSnapshot`, выбранный `source`, эффективные `parameters`, `environment`, `ports`, `storage`. Поле `definitionSnapshot` — complete component document соответствующей версии; загрузка окружения не требует брать новое содержимое из библиотеки. Одинаковое описание может порождать несколько экземпляров со своими значениями и ресурсами.

| Группа | Содержимое и правила |
| --- | --- |
| Source | Готовый image reference либо local build inputs; `infrastructure` допускает только image. Local build фиксирует context/Dockerfile и объявленные inputs; выбор пути не исполняет Dockerfile |
| Parameters | Эффективные map Value по parameterDefinitions snapshot; обязательность, тип и диапазоны проверяются локально. Secret-параметр содержит ссылку, значение в JSON запрещено; объявленный environmentVariable получает canonical string соответствующего типа |
| Environment | Явные literal/secret-reference значения; никакого произвольного кода или неограниченного шаблонизатора |
| Ports | Устойчивый локальный ID, protocol, container port, заданный/автоматический host port и access scope; default — loopback, LAN открывается явно для каждого порта |
| Storage | Устойчивый локальный ID и container target; изменяемые persisted-данные размещаются в managed named volume одного окружения/экземпляра. Внешние source/input mounts только read-only |
| Readiness | `none`, `running`, `dockerHealth` либо `http` с ограничением ожидания; `none` не допускает собственных initializer/baseline или `ready` dependency. `applicationReady` требует declared runtime readiness и всех назначенных baseline applications; отсутствие Docker healthcheck отличается от health success |

Схема source и storage не предоставляет произвольные writable host folders. Порты валидируются по protocol, host binding и числу; фактическая занятость проверяется вновь перед runtime. Для HTTP initializer может планироваться auto-assigned loopback control port: он входит в preview/operation plan и observation, но не означает разрешение доступа из сети.

Коллизия явно заданного environment key с `parameterDefinitions[].environmentVariable` является structural error; порядок слияния не должен молча выбирать значение. Необъявленный parameter key также отклоняется, а отсутствие внешнего значения secret-reference остаётся readiness issue.

### ComponentDefinition и ApplicationDefaults

`ComponentDefinition` — документ `kind: component`, `schemaVersion: 1`, UUID `id`, semver `version`, `name`, `componentType: target|infrastructure`, `contract: { id, version }`, `defaultSource`, `parameterDefinitions`, `defaultEnvironment`, `defaultPorts`, `defaultStorage`, `endpoints`, `readiness`, `dependencyRequirements`, `initialization` (embedded document либо null). Contract ID — namespace string прикладной совместимости; он отличается от UUID библиотечного документа. Библиотечная версия неизменяема: изменение создаёт новую версию; совпадение identity/version с другим hash не перезаписывает содержимое.

`ApplicationDefaults` хранит локальные начальные значения новых форм и UI preferences в отдельном документе. Новое окружение получает копию выбранных эффективных значений. Изменение библиотеки/defaults не пересчитывает сохранённые snapshots; явное применение обновлений проходит редактор и обычные правила новой ревизии.

### ComponentDependency

Единственный сохранённый resolved graph — верхнеуровневый `IsolatedEnvironment.dependencies`. Дополнительного `dependencyBindings` внутри экземпляра нет. Ребро содержит `dependentId`, `requiredId`, `requirementId`, `condition`, `reason`.

Ненулевой `requirementId` ссылается на `definitionSnapshot.dependencyRequirements` зависимого экземпляра. Requirement задаёт ID требования, точные `definitionId`/`definitionVersion`, условие и причину. `requirementId: null` обозначает явно добавленную пользователем дополнительную связь; она не подменяет обязательное requirement. `condition` — `running`, `healthy` или `ready`, с проверяемой семантикой из runtime contract. Validator проверяет:

1. Оба конца существуют в этом окружении, self-edge отсутствует.
2. Для non-null requirement Required instance соответствует требуемой библиотечной идентичности/версии; для null действуют общие правила explicit edge.
3. Обязательное requirement имеет ровно одно разрешённое назначение, условие согласовано со snapshot; null edge содержит явные condition/reason и не засчитывается как это назначение.
4. Граф ацикличен; адресная ошибка содержит цепочку цикла/отсутствия.
5. При нескольких подходящих экземплярах выбор делает пользователь; произвольный первый элемент недопустим.

Автовключение зависимости создаёт обычный конкретный экземпляр и показывает `reason`. Наличие ребра не означает runtime-готовность: scheduler ждёт его фактически проверенное условие.

## 3. Внешние входы и секреты

`InputReference` обозначает локальный source path, Dockerfile/read-only input, image reference, secret reference или точную версию пакета. Это описание требуемого входа, а не разрешение выполнить его при чтении. Ошибки локализации/доступа хранятся отдельно с code, field path, объяснением и временем проверки; в экспорт не входят.

`LocalPathReference` имеет переносимый вид `local-path://<alias>`; `LocalPathMapping` локально связывает alias с выбранным абсолютным filesystem path и временем проверки. В source/storages сохраняется ссылка, Dockerfile — относительный путь, context subdirectory проверяется относительно отображённого source root; выход за разрешённую область не допускается. Native путь не подменяется одноимённой папкой при импорте: отсутствие mapping показывается для явного разрешения. В переносимом JSON также не сохраняются вычисленные build staging paths.

`SecretReference` имеет вид `secret://<uuid>` и обозначает opaque локальный ID. `SecretRecord` связывает его с пользовательской подписью и DPAPI `CurrentUser` ciphertext; plaintext не хранится в configuration, operation journal, preview или export. Успешное разрешение даёт короткоживущий runtime/observation secret context. Потеря доступа к Windows identity или удаление vault entry создаёт unresolved reference и не вызывает подстановки другого значения.

Secret context наблюдения удерживает нужный набор известных значений всё время tail, даже после завершения Start. Redaction происходит до memory/UI/disk sink и учитывает границы chunks и объявленные sensitive fields. Если набор не подготовлен, соответствующий вывод не открывается. Raw HTTP response bodies не журналируются. DPAPI не является границей защиты от процесса того же Windows user; произвольные неизвестные секреты в неразмеченном stdout нельзя классифицировать автоматически — ограничения описаны в [research.md](research.md).

`PreviewSnapshot` связывает environment/revision, canonical effective spec, hashes initializer/pack, ID secret references, source/build fingerprints и известные image IDs/digests. Offline unknown указан явно. Token не содержит secret values. Start до runtime mutation разрешает все inputs и сравнивает snapshot; delta, включая unknown → resolved, требует отдельного подтверждения. Принятые build inputs staging/pinned images затем образуют immutable execution inputs операции.

## 4. SeedPack и baseline

### SeedPack, SeedSection и ContentBlob

`SeedPack` содержит точную identity/version/contentHash, title/theme/purpose, `compatibility: [{ contractId, versions }]`, измеренные размеры/counts и declared sections/initializers. `SeedSection` содержит `id`, `title`, относительный `path` NDJSON, `sha256`, `bytes`, `recordCount`; локальная запись добавляет ссылку на derived индекс byte offsets. Объявленный initializer entry содержит `path`, `bytes`, `sha256`. Доменные имена секций не фиксируются в приложении; совместимость проверяется по contract, а не отображаемому имени сервиса.

`ContentBlob` — immutable app-owned content по SHA-256. `contentHash` пакета — SHA-256 canonical manifest без свойства `contentHash`; hashes entry bytes уже включены в manifest. ZIP timestamps и способ сжатия не создают другую идентичность, порядок массивов manifest сохраняется. Секции хранятся в форме, пригодной для последовательного чтения и seek preview; индекс выводится из проверенного содержимого, не считается доверенным вводом архива и может быть перестроен.

Импорт проходит private staging → полную проверку состава/путей/размеров/содержимого/совместимости → promotion blobs → DB commit. До commit пакет отсутствует в библиотеке. Если commit не состоялся, unreferenced staged/promoted файлы не объявляются пакетом. Тот же identity/version/hash даёт no-op, другой hash под тем же identity/version — ошибку без изменения библиотеки.

References сохранённых окружений блокируют удаление используемой версии с перечислением окружений. Незавершённая операция также удерживает выбранные blobs и candidate references до разрешения recovery. Terminal history хранит идентичность/hash для объяснения результата и не требует бесконечно удерживать payload уже неиспользуемого пакета. Удаление библиотечной версии и освобождение её blob references согласуются транзакционно; общие blobs удаляются только при отсутствии всех ссылок и staging leases, в пределах private content directory.

### DataBaseline и BaselineApplication

Переносимый `baseline` содержит `applications`: каждое назначение — `{ componentId, initializer, seedPack }`, где `initializer` является embedded versioned document, а `seedPack` — `null` либо точный `{ id, version, contentHash }`. Назначение относится к одному instance ID. Пакет не обязателен; обязательная инициализация без пакета задаётся отдельным application с `seedPack: null`.

`baselineHash` производен от эффективного содержимого applications, initializer hashes и exact package references. Для каждого application вычисляется отдельный `applicationHash`; два полностью одинаковых назначения в baseline отклоняются как дубль. Secret-reference IDs входят в описание, plaintext — нет. Пакет сам не меняет baseline сохранённого окружения при импорте новой версии. Отсутствующий способ применения/несовместимость пакета блокирует выбор такого назначения и refresh; доступность всех inputs проверяется до удаления данных.

При импорте окружения без pack проверка его manifest/sections/initializer-entry equality откладывается в readiness issues, не запрещая сохранение переносимого snapshot. Доказанный конфликт доступного manifest — semantic error. Target contract самого initializer и instance references проверяются независимо от наличия pack.

### InitializationDefinition и InitializationStep

`InitializationDefinition` задаёт schema/identity/version, target contract и упорядоченные steps. Step содержит устойчивый ID, endpoint ID конкретного назначения, relative path/method, literal/secret-ref headers, inline JSON или section body reference, ожидаемые statuses и необязательные JSON-pointer assertions, timeout и retry policy. Ссылка на section разрешается только внутри назначенного verified SeedPack. Порядок задаёт документ, не имя demo-сервиса.

Runtime URI строится только из loopback endpoint mapping целевого instance; произвольный absolute URL, redirect, ambient proxy/credentials/cookies или локальная команда отсутствуют в контракте. Данные пакета и ответы сервиса не становятся кодом. Точные bounds/allowlists определены в [documents.md](contracts/documents.md) и [runtime.md](contracts/runtime.md).

## 5. Поколения данных и результаты наполнения

### DataGeneration

`DataGeneration` содержит ID, environment ID, baselineHash, время выделения/инвалидации, `validity`, причину инвалидации и operation ID перехода. `validity` имеет состояния `active`, `invalidated`, `retired`; наличие/полнота данных и готовность baseline вычисляются отдельно по resources и application records.

Новое окружение получает active generation без ресурсов и successes; это ещё не признак запуска. При Start generation используется для создаваемых ресурсов и записей наполнения. Stop/Start сохраняет generation. До первого destructive attempt текущая generation durable переводится в `invalidated`, а операция держит ID следующей generation. Это происходит даже когда первый deletion call впоследствии возвращает ошибку: пока результат не сверен, прежние successes использовать опасно.

| Событие | Изменение generation/configuration |
| --- | --- |
| Обычный Stop, последующий Start, observation/defaults change | Generation и revision прежние |
| Complete clear | После verified cleanup прежняя generation retired, новая active без successes; configuration/revision/baseline прежние |
| Complete edit cleanup | Один commit публикует candidate revision +1 и новую active generation; окружение остановлено и неинициализировано |
| Refresh | После cleanup выделяется новая active generation; затем временный runtime применяет baseline, проверяет результат и подтверждает остановку; revision прежняя |
| Partial cleanup/cancel/crash | Прежняя specification/revision остаётся, generation invalidated; часть resources может отсутствовать, Start блокируется до явного recovery |
| Внешняя потеря/замена persisted resource | Старые successes не удостоверяют новые данные; observation отмечает расхождение, generation инвалидируется до восстановления runtime-команд |

Необязательные metрики/логи не влияют на validity. Недоступный Docker сам по себе не доказывает потерю данных: наблюдение становится unknown/stale, deletion и editing блокируются до проверки.

### DataApplicationRecord и StepAttempt

Долговременный ключ успеха: `(environmentId, componentId, dataGenerationId, baselineHash, applicationHash, initializerHash, stepId)`. Сохраняются state, stable idempotency key, attempt number, timestamps, accepted status/assertion outcome и sanitized error code. Request/response bodies и разрешённые secret values не сохраняются. Успех первого экземпляра не засчитывается второму; успех применения одного пакета не засчитывает другой пакет с тем же initializer.

Состояния: `pending` → `started` → `succeeded` / `failed` / `unknown`; отмена до dispatch сохраняет `pending` или явный skipped outcome, после dispatch может дать `unknown`. `failed` означает известный отрицательный результат, но не обещает отсутствие побочных эффектов сервиса. Зависимые steps ждут `succeeded`.

`started` durable фиксируется до send; `succeeded` — только после ответа и проверок. Timeout, crash или cancel после dispatch не доказывают, что сервис не применил запрос. При перезапуске незакрытый started становится unknown. Уже durable succeeded той же generation не выполняется повторно.

Повтор `unknown` допускается автоматически только при объявленной безопасности запроса или server-supported idempotency key; один header не является доказательством. Ключ связывает environment/instance/generation/baseline/application/initializer/step и не меняется при retry. Иначе требуется явное решение пользователя повторить с последствиями либо refresh. Exactly-once между SQLite и внешним сервисом не обещается. Старые records другой generation не дают права пропустить новое наполнение.

## 6. Владение runtime-ресурсами

`OwnedResource` связывает installation/environment/component IDs, resource lifetime UUID, kind (`container`, `network`, `volume`), creation revision, data generation, desired attributes и actual runtime identity. Для containers/networks сохраняется actual Docker ID; для volume — exact name, lifetime UUID/labels и engine-observed `CreatedAt`, `Driver`, `Scope`, `Options`. Эти поля входят в incarnation fingerprint ledger/confirmation; mismatch или отсутствие нужного evidence блокирует deletion. V1 поддерживает только local driver/local scope без custom options. Отображаемое имя не является доказательством владения.

Имена: Compose project `eb-<envGuidN>`, service `c-<instanceGuidN>`. Labels содержат installation/environment/resource UUID, component UUID где применимо, creation revision и generation. Ledger фиксирует создание намерения до эффекта, actual identity — после наблюдения результата; unresolved create intent остаётся доступным для reconciliation.

Перед удалением проверяются ledger, exact identity, labels и все consumers, включая stopped containers. Несовпадение, shared mount или foreign consumer блокирует затронутый deletion. Ресурс того же имени после внешнего пересоздания не считается прежним. Images, source trees, import files и read-only внешние inputs не входят в удаляемые данные окружения. Приложение не выполняет blanket prune/down-v/rmi/remove-orphans.

`ResourceObservation` хранит actual state, consumers, lastCheckedAt и результат ownership check, а не меняет ledger догадкой. Лишний ресурс с matching environment labels показывается как drift/unverified до проверки; отсутствующий ledger resource показывается как missing. Признание уже отсутствующего ресурса удалённым требует реальной проверки по сохранённой идентичности, а не повторного destructive вызова.

## 7. Операции, подтверждения и восстановление

### Operation и OperationPlan

`Operation` содержит ID, environment ID, kind, state, timestamps, исходную revision/config hash, immutable `OperationPlan`, component/stage progress, sanitized results, cancellation/close request и unresolved remnants. Coordinator принадлежит приложению, поэтому navigation не теряет операцию. Один mutation lock на environment и single-writer instance на data directory предотвращают competing mutations; Readiness/observation остаются отдельным чтением.

`OperationPlan` содержит точные resources/data, необходимые stop/temporary start stages, input fingerprints, baseline/generation, expected ownership/state, последствия и candidate specification при edit. `ConfirmationRecord` фиксирует hash показанного плана и время явного решения. Материальное изменение ресурсов/состава/входов делает старое подтверждение непригодным: требуется новый plan/confirmation. Известная неподтверждённая остановка/ownership не допускают deletion.

### OperationStep и переходы

| State | Значение / дальнейшие действия |
| --- | --- |
| `planned` / `awaitingConfirmation` | Effects ещё не разрешены; отказ сохраняет configuration/runtime без изменений |
| `running` | Durable plan и intent существуют; UI показывает stage, процент только при известном знаменателе |
| `cancelRequested` | Новые stages не планируются; текущий этап достигает safe point, результат сохраняется |
| `reconciling` / `needsAttention` | Результат effect неизвестен или остались unresolved resources; обязательны наблюдение и явное решение |
| `succeeded` | Все условия успеха проверены, включая final stop для refresh |
| `failed` / `partiallyCompleted` / `cancelled` | Не полный успех; доступны фактические эффекты, остатки и дальнейшие действия |

Step хранит ordered ID, action/target, durable intent, state, timestamps, actual identity/result и error. `started` фиксируется commit до любого изменяющего Docker/HTTP вызова. `observedResult` фиксируется отдельным commit после effect и сверки. Сбой записи intent запрещает effect; сбой сохранения result прекращает scheduling и оставляет unknown для recovery.

При запуске приложения незавершённые шаги сопоставляются с Docker/resources и persisted records. Чтение и reconciliation не повторяют destructive stages. Resume требует актуального плана и пользователя; ambiguous HTTP mutation без safety не повторяется автоматически. Cancel не является rollback: удалённые данные не восстанавливаются, работающие компоненты после ошибки Start не останавливаются автоматически.

Обычное закрытие хранит пользовательский выбор ждать или безопасно отменить. App закрывается после safe points, завершения/отмены всех active операций и commit результатов. Невозможность прервать текущий короткий server mutation отображается как ожидание safe point, а не как выполненная отмена. Forced exit оставляет durable незавершённый journal.

`DeletionTombstone` сохраняет environment ID/последний snapshot, plan и ledger remnants до verified отсутствия всех scoped ресурсов. Partial delete не скрывает окружение. После полного удаления terminal history может сохранить только sanitized summary без живых resource/blob references. Отменённый/failed edit не заменяет authoritative snapshot кандидатом.

## 8. Наблюдения и конечные объёмы

`EnvironmentObservation` содержит environment ID, session ID, checkedAt/lastSuccessfulAt, freshness (`current`, `stale`, `unknown`), actual components и drift. Component observation содержит Docker status, отдельный health (`notConfigured` отличается от `healthy`), ports/addresses и полученные measurements. `Measurement` — value/unit/time или явно missing reason; нулевое измерение не равно missing. Aggregates содержат completeness/count покрытия и не выдают частичный итог за полный.

Один active Inspect session владеет tail, metrics, timers и relevant secret context; переключение отменяет/ожидает/dispose предыдущую session. Late result с другим session/environment ID не применяется к новой VM.

| Данные | Принятое удержание |
| --- | --- |
| Logs | До 10 000 строк или 8 MiB на observation; 32 MiB на приложение, одна строка до 16 KiB; truncation/drop counters видимы |
| Metrics | 300 samples/component с интервалом 2 s; отсутствующие значения не заполняются нулями |
| Seed preview | 100 records либо 256 KiB/page, record до 64 KiB; cache 8 MiB |
| Diagnostic files | Пять файлов по 4 MiB, redaction до записи |
| Terminal operations | Новейшие 1 000 globally; unresolved operations, tombstones и successes active generation не удаляются этой политикой |

Ограничения archives/documents/HTTP дополнены в [documents.md](contracts/documents.md) и [runtime.md](contracts/runtime.md). Bounded channels, batch UI updates и counters относятся к session state; raw container logs не сохраняются в SQLite. Unresolved ledger не является log history и не обрезается ради лимита отображения. Отсутствие прежней session означает отсутствие данных графика, а не измеренный ноль.

## 9. Локальная SQLite-модель

Одна `%LOCALAPPDATA%/EnvironmentBuilder/state.db` является authoritative. JSON документы сохраняют переносимую форму; небольшие relational поля/indexes обеспечивают поиск, ссылки и recovery. EF/repository framework/event sourcing не требуются.

| Таблица | Ключ / основные поля / связи |
| --- | --- |
| `AppMetadata` | key/value: installation ID, DB schema version, format support metadata |
| `Environments` | environment ID PK; current revision, JSON, hash, summary indexes, current generation ID, deletion state; ID не зависит от name |
| `ComponentDefinitions` | `(definitionId, version)` PK, content hash, JSON; immutable после insert |
| `ApplicationSettings` | key PK, versioned defaults/UI JSON; не источник для повторного открытия effective environment |
| `LocalPathMappings` | alias PK, absolute local path, last verification metadata; переносимый документ хранит только `local-path://` reference |
| `Secrets` | secret reference PK, label, DPAPI ciphertext, timestamps; plaintext column отсутствует |
| `SeedPacks` | `(packId, version)` PK, contentHash, manifest JSON, verified metadata |
| `PackEntries` | `(packId, version, path)` PK; section/initializer ID, blob hash, sizes/counts, derived index location; FK на pack |
| `ContentBlobs` | SHA-256 PK, private relative location, verified size; references от PackEntries и active staging leases |
| `PackageReferences` | owner kind/ID + component/application ID + exact pack key/hash; owners — saved environment и recovery-relevant operation |
| `DataGenerations` | generation ID PK, environment FK, baseline hash, validity/reason/timestamps/operation ID |
| `DataApplicationRecords` | Unique success key из §5, state/attempt/key/timestamps/sanitized result; FK на generation/environment |
| `Operations` | operation ID PK, environment ID, kind/state, original revision/hash, confirmed plan JSON, candidate JSON, timestamps/result; delete history не каскадирует unresolved evidence |
| `OperationSteps` | `(operationId, stepId)` PK, ordinal, target/action, intent/result, state/timestamps; FK на operation |
| `ResourceLedger` | resource lifetime UUID PK, ownership fields, desired/actual identity, create/delete intent, unresolved state; environment/generation/operation relations сохраняются для tombstones |

Component IDs внутри authoritative JSON проверяются domain validator до транзакции; не создаётся отдельная копия editable component configuration. Search/type summary и PackageReferences — производные проекции JSON, обновляемые тем же commit. Для FK к environment при удалении применяется явная финализация/сворачивание history, а не cascade, уничтожающий journal или remnants. Полные прошлые ревизии не накапливаются как неявный архив.

WAL, `synchronous=FULL`, foreign keys, short serial transactions и проверка native SQLite применяются на persistence worker. Нельзя держать DB transaction открытой во время Docker, HTTP, preview ожидания или пользовательского подтверждения. Database schema version независима от `schemaVersion` JSON; поддерживаемые DB migrations выполняются транзакционно до normal writes, неподдерживаемая DB version не изменяется.

### Границы commit

| Сценарий | Атомарный локальный commit / внешний effect |
| --- | --- |
| Create/import environment | Проверить весь JSON → один commit environment + generation + references + summary; runtime отсутствует |
| Library/default update | Validate → один commit новой версии/настроек; saved snapshots не меняются |
| Import SeedPack | Полная staging validation/promotion → один commit pack/entries/references; при ошибке не появляется частичный pack |
| Confirm destructive operation | Один commit immutable plan/confirmation/candidate → вновь проверить фактическое состояние → commit step intent + generation invalidation перед первой deletion попыткой |
| Runtime stage | Intent commit → external effect → observation/reconciliation → result commit; общей distributed transaction нет |
| Edit finalization | После verified полной cleanup один commit candidate revision +1, fresh generation, refs/indexes и operation result; до commit текущая specification прежняя |
| Clear/refresh reset boundary | После cleanup один commit fresh generation и retirement прежней; clear завершается, refresh продолжает init с новым journal |
| Delete finalization | После отсутствия всех scoped ресурсов один commit убирает live description/refs и сворачивает tombstone; unresolved evidence не удаляется |
| Init step | Started commit → HTTP → assertion validation → success/error/unknown commit |

Если эффект состоялся, но следующий commit не удался, запись остаётся незавершённой и требует reconciliation. Если все deletion effects прошли, но публикация edit revision не состоялась, old revision остаётся текущей; durable candidate и verified результаты позволяют явно завершить финализацию после повторной сверки без повторной очистки.

## 10. Повреждения, неподдерживаемые форматы и retention ссылок

Каждая environment/library JSON row читается независимо с bounded strict reader. JSON parse/semantic error или неподдерживаемый `schemaVersion` даёт отдельный unreadable/unsupported item с устойчивым ID и адресной причиной; остальные корректные окружения остаются доступны. Ошибка не вызывает импорт defaults, generation reset или перезапись исходного JSON. Summary index позволяет показать запись, не выдавая повреждённую конфигурацию за валидную. Неудачный save не заменяет последнюю корректную row благодаря transaction.

Повреждение самого SQLite-файла отличается от повреждения одного документа: если БД нельзя безопасно прочитать, приложение показывает database-level diagnostics и блокирует mutation вместо обещания изоляции повреждённых страниц. Нельзя автоматически создавать пустую БД поверх недоступной/повреждённой.

Retention удаляет только terminal history, не needed-for-safety evidence. Live descriptions, active generations/success markers, unresolved operations и tombstones удерживают свои необходимые refs. Inactive generation records могут сворачиваться вместе с незащищённой terminal history; они не используются для новых Start. Unreferenced pack staging/index files очищаются только в проверенных private app directories; это не recovery deletion runtime-данных окружения. Отсутствующий/corrupt blob или index диагностируется: индекс можно перестроить по verified blob, payload нельзя подменить другой версией пакета.

Эта модель обеспечивает FR-002/009/011–014/018/020/023/025–038/040 без обещания восстановления уже удалённых данных. Проверки переходов, fault points и соответствия schema/runtime reader включаются в [quickstart.md](quickstart.md) и последующие implementation tasks.
