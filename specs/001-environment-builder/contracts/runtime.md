# Runtime contract v1 — Environment Builder

Нормативный контракт для будущей реализации. Он дополняет [spec.md](../spec.md), [plan.md](../plan.md), [research.md](../research.md), [модель данных](../data-model.md) и [форматы документов](documents.md). Здесь определены действия и наблюдения; JSON schemas описывают переносимые документы. Ни один сценарий ниже не считается выполненным фактом до реализации и проверки по [quickstart.md](../quickstart.md).

## 1. Границы исполнения и поддержка

- Authoring, сохранение, импорт/экспорт и Compose/SeedPack preview работают без Docker и не запускают процессы, контейнеры, Dockerfile, registry lookup или HTTP. Чтение JSON, bounded preview и hashing разрешены; содержащиеся в источнике команды никогда не интерпретируются приложением.
- Runtime поддерживает локальный Docker Desktop на Windows 11 x64 обслуживаемых выпусков 24H2+, WSL2, Linux containers. Минимум Engine 28.0.0/API 1.48 и Compose 2.24.6. Принимаются major 2 и 5 с успешным capability probe. Остальные комбинации получают диагностируемое ограничение; версия выше минимума не доказывает совместимость.
- Минимальная конфигурация и актуальный стенд Desktop 4.96.2/Engine 29.9.1/Compose 5.6.x проверяются отдельно. Наблюдённый локально Compose 5.4.0 допустим для probe, но daemon с `permission denied` остаётся недоступным и непроверенным.
- Coordinator принадлежит приложению и сохраняет операции при навигации. Один OS mutex на канонический data directory допускает только одного writer; второй процесс активирует существующее окно либо сообщает о занятом хранилище, не начинает независимые операции. Изменения сериализуются по неизменяемому environment ID; разные окружения могут работать независимо. Потеря mutex/DB write capability запрещает новые эффекты.

## 2. Docker CLI boundary

Один infrastructure adapter запускает найденный и проверенный абсолютный путь `docker.exe` через `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, без `cmd`, PowerShell, shell quoting и конкатенации аргументов. stdout/stderr читаются асинхронно одновременно, процесс не получает консоль или TTY. Exit code, timeout и stderr — отдельные данные; любой вывод проходит redaction до UI, очереди хранения и diagnostics. Секретные значения не передаются в arguments, names или labels.

Перед runtime adapter читает выбранный context, принимает только `npipe` endpoint на локальном `.` host, затем проверяет server version, API range, Engine ID и `OSType=linux`. Например, `npipe:////./pipe/dockerDesktopLinuxEngine` и `npipe:////./pipe/docker_engine` допустимы только после фактической проверки; строка context name сама по себе не удостоверяет engine. TCP, SSH, Unix socket, remote npipe, Offload, Windows containers и отсутствие доступа блокируют runtime, сохраняя authoring.

Endpoint и Engine ID фиксируются в operation plan. Каждый последующий вызов получает явный `--host`; приложение не меняет глобальный текущий context. Перед первой мутацией и каждым разрушительным этапом Engine ID сверяется снова. Смена ID/endpoint прерывает исполнение до новой сверки и, если изменился план, нового подтверждения. После daemon restart неизвестное состояние нельзя считать прежним подтверждённым состоянием.

Child environment очищается от `DOCKER_CONTEXT`, `DOCKER_HOST`, `DOCKER_API_VERSION`, routing/TLS/default-platform overrides и `COMPOSE_*`, влияющих на project/files/profiles/interpolation/parallelism. Нужные proxy/registry authentication settings применяются явно и не попадают в diagnostics; приложение не переписывает пользовательский Docker config. Для Compose всегда заданы `-f`, `-p`, `--project-directory`, `--env-file`, `--ansi never` и предсказуемый progress mode. Private рабочий каталог не содержит пользовательских `.env`/override файлов; ненужное наследование interpolation исключено.

Пример логического массива arguments для запуска одного экземпляра; каждый элемент передаётся отдельно, пути не становятся shell text:

```text
["--host", endpoint, "compose", "--ansi", "never", "--progress", "plain",
 "--project-directory", privateDirectory, "--env-file", privateEnvFile,
 "-f", privateComposeFile, "-p", projectName,
 "up", "-d", "--no-deps", "--no-build", "--pull", "never", serviceKey]
```

`projectName = eb-<environmentGuidN>`; `serviceKey = c-<componentInstanceGuidN>`. Отображаемые имена и definition IDs не участвуют в адресации. Два экземпляра одного definition — два service keys, отдельных набора параметров и данных, не `--scale` одного сервиса.

Probe выполняется только при явно запрошенной runtime/preflight/диагностической операции. Он проверяет version output, поддержанные flags `config --format json`, `up -d --no-deps --no-build --pull never`, JSON list/inspect, bounded logs и stats output. Безопасный sentinel Compose для parser probe не содержит пользовательских builds, secrets, hooks или external includes. Нестабильный/неизвестный формат вывода не угадывается: affected capability недоступна с причиной. Probe сам не создаёт runtime-ресурсов. Compatibility fixtures покрывают minimum, Compose 5.4 и актуальный major 5; успешная проверка CLI без server не означает успешный runtime probe.

## 3. Offline compiler, preview и проверка входов

Чистый compiler получает effective immutable snapshot окружения, развёрнутые component/init snapshots и baseline refs. Он валидирует граф, конкретные instance IDs, параметры, endpoint/port/storage mapping и выдаёт полный Compose model, план зависимостей и redacted preview. `parameterDefinitions[].environmentVariable` переводит effective parameter в container environment: integer/boolean преобразуются invariant в строку, secret parameter остаётся ref до runtime. Совпадение с явно заданным `components[].environment` key или двумя parameter mappings — адресная ошибка, без скрытого приоритета. Он не читает изменяемые defaults вместо сохранённых значений и не вызывает Compose как renderer. Строковые литералы сериализуются безопасно, символ `$` не превращается в неявную подстановку из окружения процесса. Preview и private runtime rendering строятся одним compiler из одного effective model; различие состоит в разрешении secret refs и фиксированных runtime identities.

Preview token содержит SHA-256 канонического представления следующих входов:

| Вход | Идентичность в token |
| --- | --- |
| Окружение | environment ID, revision, complete effective snapshot и версия compiler contract |
| Наполнение | initializer content hashes, baseline identity, выбранные точные SeedPack identity/version/contentHash и назначения instance ID |
| Секреты | opaque reference IDs; никаких plaintext values или их hash в переносимом preview |
| Local build | разрешённый `pathRef`/`contextSubdirectory`, sorted relative paths/content hashes, `dockerfile`, `.dockerignore` и Dockerfile-specific ignore, literal `buildArguments`, `buildSecrets` ref IDs, зафиксированная platform `linux/amd64`; v1 использует final Dockerfile stage |
| Read-only input mounts | `pathRef`, container target и hash фактически используемых file/tree bytes; неизвестность доступа не подменяется пустой папкой |
| Готовый image | объявленный ref/platform и известные local image ID/registry digest; отсутствие подтверждённой identity выражено `unknown` |
| Внешняя готовность | availability и причина неизвестности; время последней проверки отображается отдельно от content identity |

Неизвестные inputs не мешают структурно корректному сохранению, но блокируют выполнение зависящего этапа. `unknown` явно входит в token; `unknown → resolved` — существенная delta. При preview без Docker запрещено скрытно выполнять registry lookup. Hash source tree не заменяется timestamps, Git HEAD или `compose config --hash`: они не обнаруживают все изменения содержимого.

Start до **любой** мутации containers, networks, volumes или бизнес-данных выполняет полный preflight всех экземпляров: доступность paths/secrets/packages/providers, структурная и semantic validation, image identity resolution, fingerprint сборки, compatibility и ownership existing resources. Если после просмотренного preview inputs изменились, UI показывает конкретный diff и запрашивает отдельное явное подтверждение нового token. Отказ заканчивает попытку без runtime mutations. Если baseline token отсутствует, используется review нового полного плана перед выполнением. Диагностическое обновление не считается таким подтверждением.

После подтверждения inputs закрепляются:

1. Source tree и read-only inputs копируются в приватные app-owned staging contexts; исходные папки доступны приложению только для чтения. Reparse/symlink escape за выбранный root, неоднозначный path, read error или изменение во время capture дают readiness error. Stable snapshot хешируется по фактически скопированным bytes; несовпадение с подтверждённым token требует повторного review до build/runtime effects. `.dockerignore` сохраняется как build input. Допускается консервативно хешировать больше файлов, чем Docker отправит в build, но нельзя пропустить файл, доступный build. Read-only runtime mounts используют проверенные staging bytes с `read_only:true`, а не изменяемый внешний original path; staged mount живёт до удаления использующего его контейнера и удаляется только в private app directory.
2. Runtime build использует только staged context, зафиксированные literal `buildArguments`, platform `linux/amd64` и final Dockerfile stage. `buildSecrets` разрешаются в BuildKit secret mounts через private files с current-user ACL: plaintext не попадает в build arguments, image labels или journal. Все private secret files удаляются после завершения build client. Вывод build проходит redaction; Dockerfile не получает обещания, что не сможет явно записать полученный секрет в собственный output/image. Пользователь отвечает за доверенный Dockerfile, а приложение не сохраняет его raw output. Produced image ID записывается в journal; общий image cache не объявляется environment-owned data. Mutable network downloads внутри Dockerfile не получают обещание детерминизма: фиксируется фактический результат сборки.
3. Image refs разрешаются в digest для выбранной platform либо в exact local image ID. Pull относится к подготовке образов и завершается до запуска компонентов. Private launch model содержит pinned identity; `up` не имеет права повторно build/pull и не следует сдвинувшемуся tag.
4. Если зафиксированные bytes/image стали недоступны, операция завершается до затронутого эффекта. Нельзя незаметно подменить их свежим tag/source. Изменение уже подтверждённого runtime/destructive плана требует нового подтверждения.

Plaintext runtime Compose/env files создаются только после разрешения операции с current-user ACL в private directory, не экспортируются и не логируются. Удаляются после последнего использующего процесса; при startup выполняется очистка orphan private files после сверки активных/прерванных операций. Неудача cleanup видима без раскрытия содержимого.

## 4. Start, готовность и Stop

Application scheduler обходит DAG concrete instances. Edge из `dependencies` содержит `dependentId`, `requiredId`, `condition`, `reason` и `requirementId`; последнее равно ID обязательного declaration requirement либо `null` для ручной связи. Для каждого обязательного requirement выбирается exact instance; цикл, missing target, self-edge и неоднозначность блокируют сохранение/запуск. Порядок не выводится из имени сервиса.

| Edge `condition` | Условие допуска зависимого компонента |
| --- | --- |
| `running` | Required container фактически running; отсутствие readiness не подменяет health |
| `healthy` | Docker healthcheck существует и фактически `healthy`; нет healthcheck — structural/readiness error с причиной |
| `ready` | Required component достиг `applicationReady`: собственная declared runtime readiness проверена и все назначенные baseline applications, включая mandatory initialization и выбранные packs текущей generation, завершены |

Component `readiness.kind` — `none`, `running`, `dockerHealth` или `http`. Последние три задают `runtimeReady`; HTTP readiness — bounded GET declared endpoint/path с ожидаемым успешным 2xx status, без body mutation и с той же loopback policy, что §8. `none` отображается как «проверка не задана» и не даёт подтверждённого ready/healthy; edge `ready` к такому компоненту отклоняется с предложением выбрать осознанное условие `running` либо объявить readiness. Component с `none` не может иметь собственный initializer или назначенный baseline application: semantic validation требует явное `running`/`dockerHealth`/`http`, прежде чем разрешить initialization. Он может быть запущен и наблюдаем, но fully-ready статус ему не приписывается.

Граф этапов: Start container → `runtimeReady` → все назначенные baseline applications → `applicationReady`; initializer ждёт собственную runtime readiness и предыдущие steps, **не** собственный `applicationReady`. Это исключает скрытый цикл «готовность требует initialization, initialization требует готовность». Mandatory descriptor initialization с `seedPack:null` выполняется ровно один раз прежде pack applications данного instance; далее применяется порядок baseline applications. `applicationReady` требует завершения всех назначенных mandatory и выбранных pack applications. Ошибка initialization блокирует `ready` dependents, но не останавливает уже запущенные независимые components.

Для готовой вершины adapter выполняет detached `up` из §2 с `--no-deps`; остальные dependency stages запускает coordinator. После exit выполняется inspect и отдельное bounded ожидание readiness с timeout из declaration. Каждое неготовое условие видно с instance ID и причиной. Независимые вершины могут выполняться с ограниченным parallelism, но первый failure прекращает выдачу новых изменяющих этапов; уже принятые эффекты сверяются. Инициализация ждёт всех заявленных условий и предыдущих шагов.

Attached `up`, `--abort-on-container-exit`, `--abort-on-container-failure`, `--exit-code-from`, implicit watch, hooks и автоматический rollback не применяются. Ошибка запуска/наполнения не вызывает Stop/cleanup успешно работающих компонентов. Итог отражает частичный состав и доступный Inspect; `Succeeded` означает выполнение всех обязательных этапов и readiness, а не только exit code CLI.

`ConfirmedQuiescent` — проверенное отсутствие running/restarting/paused/unknown owned containers и конфликтующей операции; verified empty resource set также удовлетворяет guard. Это допускает редактирование сохранённого, ещё не запускавшегося окружения после проверки отсутствия ресурсов, сохраняя отдельный display NeverStarted. При недоступном Docker guard не подтверждается.

Stop адресуется exact verified owned container IDs, включая обнаруженные owned остатки, и выполняется в обратном порядке зависимостей. Он не удаляет containers, volumes, images, specification или success markers. Политика graceful timeout фиксируется в операции; достижение Docker stop timeout может завершить process контейнера, что отражается в результате. После вызова все owned containers перечитываются: остановка подтверждена только если нет running/restarting/paused/unknown компонента и нет незавершённой конфликтующей операции. Недоступный Docker или неполный inspect дают unknown и блокируют edit/clear/refresh. Исчезнувший контейнер — observed missing, не доказательство сохранности его данных.

Повторный Start использует сохранившиеся данные и не повторяет durable successful init steps той же data generation. Обнаружение отсутствующего/чужого volume или нарушения generation инвалидирует прежнюю готовность; приложение объясняет расхождение и не сертифицирует пустые данные прежним success marker.

## 5. Владение и destructive plan

Каждый owned container, private bridge network и managed named volume получает labels installation ID, environment ID, resource UUID, creation revision и data generation; component instance ID добавляется к component resources. Namespace приложения не использует reserved Docker labels. Ledger сохраняет create intent до эффекта и exact actual identity после: container/network IDs; для volume — exact name, lifetime resource UUID/labels и полученные от Engine `CreatedAt`, `Driver`, `Scope`, `Options`. Incarnation fingerprint сохраняется после create и входит в confirmation; отсутствие/изменение этих данных блокирует deletion, даже если имя/labels совпали. Только local driver/local scope без custom options поддерживается v1. Compose project/service labels — дополнительное свидетельство, недостаточное самостоятельно. Existing resource с совпавшим именем и другой/неполной identity не принимается и не перезаписывается.

Persisted data размещаются в managed named volumes. Bind inputs только read-only, не являются данными для cleanup; arbitrary writable host directories, anonymous/external/shared volumes и произвольные driver options не входят в managed deletion contract. Images/cache/source trees никогда не включаются в cleanup plan.

Destructive plan — immutable preview конкретной операции с operation ID и token, содержащий:

- environment ID/name/current revision и candidate revision, если применимо;
- endpoint/Engine ID, observation time, требуемую подтверждённую остановку;
- exact resource identities/labels, component associations, volume names и известные attachments;
- удаляемые данные и init markers, будущую generation, необратимость и отсутствие backup/rollback;
- упорядоченные stop/remove/recreate/apply/verify stages и временные Start/Stop для refresh;
- известные missing/foreign/shared/unknown blockers и последствия partial failure.

Сначала показывается план и получается явное подтверждение. Затем под environment lock повторно проверяются revision, Engine ID, отсутствие conflict, фактическая остановка, ledger/labels/exact identity и consumers **всех** контейнеров, включая stopped containers, а также network attachments. Foreign/shared consumer блокирует затронутое удаление. Отсутствие доступа для полной проверки равно unknown, а не отсутствию consumers. Скопированные labels не разрешают действие без соответствующего ledger/actual identity.

Перед каждым individual deletion проверяются его identity, stop и consumers снова. Изменившийся состав/существенные условия делают token устаревшим и требуют нового плана/подтверждения. Docker не обеспечивает общей transaction с DB: отказ `volume in use`, исчезновение ресурса или внешняя гонка обрабатываются как наблюдаемый результат, а не повод использовать force. Отсутствующий ранее verified resource можно отметить already absent; автоматически добавить новый ресурс в подтверждённый scope нельзя.

Только exact address mutations: удалить verified owned stopped containers, затем их verified named volumes и private network после проверки отсутствия foreign attachments. Не использовать общий `prune`, `down -v`, `--remove-orphans`, `--rmi`, force-removal, deletion по префиксу имени либо массовый label-only filter. После каждого эффекта сохраняется outcome; после всей операции повторно проверяется отсутствие всех scoped ресурсов.

## 6. Edit, clear, refresh и delete

| Команда | Предусловия и исполнение | Успех | Ошибка/частичное исполнение |
| --- | --- | --- | --- |
| Save нового | Структурно корректный snapshot; внешние readiness issues допустимы | ID/revision 1 сохранены; Docker не меняется | Последняя корректная DB state сохраняется |
| Save существующего без изменений | Canonical persisted snapshot совпадает | No-op; нет cleanup и revision increment | Никакого destructive effect |
| Save изменённого | Подтверждённо остановлено; показан план потери данных; cleanup старой runtime generation | Только после verified cleanup одной DB transaction публикуется candidate revision; ID прежний; состояние stopped/uninitialized | Прежняя revision остаётся authoritative; candidate/draft и journal доступны; удалённое и остатки перечислены; прежние success markers не удостоверяют неполные данные |
| Очистить данные | Подтверждённо остановлено; план всех owned persisted stores; remove containers, освобождающий volumes, включён в план | Данные и init markers удалены; specification/revision/baseline сохранены; stopped; следующий Start создаёт/наполняет новую generation | Partial/unknown, остатки доступны; запуск/повтор требует сверки и явного продолжения |
| Восстановить baseline | Подтверждённо остановлено; до удаления проверены все immutable packs, compatibility, secrets, providers/endpoints и initialization; подтверждены cleanup и temporary Start/Stop | Новая generation очищена, baseline/init применены и проверены; temporary components фактически остановлены; revision прежняя | Partial restoration с outcome каждого шага/actual running state; нет rollback; автоматическая остановка работающих компонентов после ошибки наполнения не выполняется |
| Удалить окружение | Подтверждённый exact plan; если работает — verified Stop прежде removal | Все scoped resources отсутствуют; запись удалена/закрыта в authoritative store | Доступная deletion tombstone содержит specification identity, удалённое, остатки и journal; чужое не затронуто |

У refresh без SeedPack baseline означает пустые данные и обязательную initialization. Отсутствующий pack/provider блокирует действие **до** удаления. Автоматически выбранные control endpoints и temporary components перечисляются заранее; successful refresh не считается завершённым, если финальный Stop неизвестен/не удался. На partial/cancel пользователь видит работающие temporary components и отдельную доступную команду Stop. Повтор destructive workflow использует новый подтверждённый план после reconciliation, безопасен для already absent resources и не обещает повторить утраченные данные.

## 7. Журнал, cancellation и recovery

До каждого изменяющего Docker/HTTP effect persistence worker фиксирует intent в коротком durable commit. Intent включает operation/environment/revision/generation, stage/instance/resource identities, plan token, retry safety и started time; **не** request/response body, secret или raw process output. Неудача intent commit запрещает эффект. После вызова отдельный commit сохраняет exit/outcome и проверенное actual state. DB transaction не удерживается во время CLI, HTTP или user confirmation.

До первого destructive attempt старая generation и её init-success перестают удостоверять готовность, даже если удаление затем частично не удалось. Новая generation не получает `ready`, пока все обязательные условия не проверены. Crash между effect и result commit оставляет stage ambiguous; app startup читает journal, проверяет engine/owned resources и показывает reconciliation result. Операция с незавершённым intent не становится автоматически success/cancelled.

- Read-only input capture, readiness waits, logs/stats и ещё не отправленный HTTP можно отменить безопасно.
- Короткий mutation acceptance CLI stage не отменяется токеном посередине. Cancel request прекращает дальнейшее scheduling, UI объясняет ожидаемый safe point. После выхода CLI или timeout выполняется reconciliation. Если зависший клиент требуется завершить, это отмечается как unknown effect; killing client не означает, что daemon ничего не сделал.
- HTTP cancel/timeout после dispatch может оставить изменённые данные (§8). Уже удалённое не восстанавливается; cancellation не является rollback.
- После crash не выполняются автоматические destructive replay и retry неоднозначного unsafe initialization. Read-only reconciliation допустима; продолжение требует явного решения и нового подтверждения, если plan изменился. Durable success для той же generation не переигрывается.
- Result различает success, cancelled до effects, cancelled с фактическими изменениями, failure, partial и unknown/requires reconciliation. UI показывает actual changes, остатки и доступные дальнейшие команды.

Обычное закрытие окна при active operations показывает «дождаться» либо «безопасно отменить и закрыть». Навигационная cancellation не отменяет operations. При выборе ожидания окно сохраняется до terminal result. При выборе отмены coordinator запрашивает cancel, достигает safe points, фиксирует результаты и закрывает окно только после завершения/сверки либо после объяснения невозможности прервать текущий этап. Приложение не становится незаметным background worker. Принудительное завершение ОС оставляет journal для startup recovery. Неподтверждённая запись результата не считается безопасно завершённой операцией.

## 8. Декларативный HTTP initializer

Initializer — versioned external JSON по [initialization.schema.json](initialization.schema.json), не исполняемый plugin. Каждый упорядоченный step содержит `id`, `title`, `endpointId`, `method`, `path`, `headers`, `body` (`kind: none | inline | seedSection`), `expect` (`statuses`, `assertions`), обязательный `timeoutSeconds` и `retry` (`mode: none | idempotent | key`, `maxAttempts`, `safetyEvidence` для безопасных режимов и `idempotencyHeader` для key). Методы: GET/HEAD/POST/PUT/PATCH/DELETE; GET/HEAD требуют `body.kind:none`. `inline.value` — literal JSON; `seedSection.sectionId` — секция ровно назначенного этому application SeedPack. Shape/semantic rules и immutable snapshots согласуются с [documents.md](documents.md); максимум 1 000 steps, timeout 1–600 s, форма предлагает 30 s, максимум 3 attempts. Отсутствующий обязательный timeout не дописывается молча importer. Нет shell, expressions, arbitrary file refs и service-name branches в interpreter.

`endpointId` разрешается только в declared endpoint конкретного component instance. URL строится из фактически inspected published mapping с `127.0.0.1`, TCP и container port нужного endpoint. Если control port не опубликован пользователем, compiler добавляет auto-assigned loopback mapping, явно видимый в preview/plan. После create inspected host port закрепляется на время запроса; неизвестная/foreign mapping блокирует send. Пользовательский LAN mapping не используется для initialization; для него создаётся отдельный loopback control mapping.

`path` — request target внутри endpoint с начальным одиночным `/`, без scheme/authority, `//`, обратных слешей, fragment и traversal после percent decoding. Нельзя передать imported абсолютный URL, credentials, host header или переопределить endpoint через headers. `endpoint.basePath` равен `/` или slash-prefixed path без trailing slash/query/fragment; итоговый path составляется как basePath без завершающего `/` плюс step.path, без Uri resolution, отбрасывающего prefix (`/v1` + `/admin` → `/v1/admin`). Query остаётся частью явно объявленного relative request target, без runtime templating; secret refs разрешены только в предусмотренных полях. Разрешённые headers сериализуются явно; запрещены transport/hop-by-hop/Host overrides. Literal и secret-ref значения разделены DTO.

HTTP client: redirects выключены; proxy/cookies/ambient/default credentials выключены; controlled loopback only; никакого follow внешнего Location. HTTPS использует обычную validation цепочки/hostname, bypass certificate checks запрещён; loopback endpoint обязан иметь подходящий доверенный сертификат. Header/body/response parsing ограничены. Inline JSON — ограниченный literal; `seedSection` читает declared проверенную section streaming, без загрузки пакета целиком. Request body не сохраняется. Response ≤1 MiB; превышение, timeout, незаявленный status, invalid JSON либо failed assertion — failure/unknown согласно факту dispatch, без raw body в error. `expect.assertions[]` содержит только `pointer` и literal `equals`: JSON Pointer выбирает ровно одно значение, затем выполняется структурное JSON equality без type coercion; missing pointer — failure. Если assertions пусты, body не обязан быть JSON; успешный expected 204/HEAD не считается parse error. Нет expression evaluator.

Перед send durable marker Started сохраняется для `(environment, instance, data generation, baselineHash, applicationHash, initializerHash, stepId)`. `applicationHash = SHA-256(canonical baseline application)` различает применения одного initializer к двум SeedPack; он вычисляется внутри приложения и не добавляется полем external format. Полностью одинаковые baseline applications отклоняются как duplicate, порядок разных applications сохраняется. После exact expected status и assertions фиксируется Succeeded. Следующий зависимый step не отправляется до этого commit. Успешные steps той же generation повторно не отправляются при обычном Start; новая generation требует их выполнения заново.

| Retry mode | Разрешение и поведение |
| --- | --- |
| `none` | Один attempt. Dispatch с потерянным/непроверенным ответом, timeout/cancel/crash даёт Unknown; никаких автоматических повторов |
| `idempotent` | `safetyEvidence` явно документирует безопасную повторяемость данного service action; HTTP verb сам по себе не превращает изменяющий данные endpoint в доказанно безопасный |
| `key` | `safetyEvidence` подтверждает server-side deduplication; stable key выводится из полного tuple с `applicationHash`, его значение/header сохраняются до первого dispatch и повторяются без изменений для того же step/generation; `idempotencyHeader` не дублирует explicit header; один header без server support недостаточен |

Retry разрешается только после bounded transient transport failure либо явно определённых retryable statuses (408/429/502/503/504); остальные неожиданные 4xx/5xx и assertion mismatch не получают implicit retry. Используются максимум `maxAttempts`, ограниченный backoff и bounded `Retry-After` в общем timeout budget; cancellation проверяется перед новым send. После исчерпания лимита результат Unknown, если применение могло произойти, иначе Failed. Для Unknown unsafe step пользователь явно выбирает признать проверенный результат, осознанно повторить с предупреждением о дублях либо выполнить новый refresh; молчаливое продолжение запрещено. Exactly-once между SQLite и сервисом не обещается.

## 9. Наблюдения, metrics и retention

Read-only reconciliation читает actual containers/networks/volumes по ledger/labels и сравнивает с snapshot. Состав показывает expected, actual owned, missing, unexpected owned и foreign conflicts отдельно. Lifecycle, readiness, operation result и initialization state — независимые значения. `unknown`/`stale` имеют timestamp последней успешной проверки и причину; недоступный Docker не превращается в «остановлено». При восстановлении связи выполняется новая сверка.

Inspect показывает exact instance, Docker status, health (`notConfigured/starting/healthy/unhealthy/unknown`; Docker `none` переводится в `notConfigured`), actual published ports и проверенные host addresses. Для Linux VM внутренний container IP не выдаётся за доступный Windows URL. Метрики отсутствуют для stopped/missing/недоступного источника; это `unavailable`, не 0. Окружение с частичными measurements получает partial aggregate с `measured/expected` и временем.

CLI stats запрашиваются только для verified IDs с явным JSON format; строки с user-configured formats не парсятся. Sampling — каждые 2 s без перекрывающихся requests, до 300 samples на component; skipped sample отмечается, интервалы не заполняются нулями. Значения описываются следующим образом:

| Метрика | Единицы и смысл |
| --- | --- |
| CPU | Docker-reported `%` использования CPU; может быть >100% при нескольких logical CPUs. Не нормализовать самовольно до 0–100. Source unavailable/некорректное значение остаётся unavailable |
| Memory used/limit | CLI Linux memory usage после собственного исключения cache Docker CLI, limit — container limit; не называть host/process working set. `%` относится к этому limit |
| Network I/O, block I/O | Cumulative received/sent/read/written из CLI, не скорость. Rate можно вычислять только по двум измеренным samples одной container identity с положительным interval и без counter reset |
| PIDs | Измеренное целое число processes/kernel threads по Docker; не business requests и не только OS processes |

CLI отдаёт rounded strings. Adapter сохраняет original measured value/unit, parse status, sample time, container ID и source resolution; нормализует decimal units (`kB/MB/GB`) через 1 000, binary (`KiB/MiB/GiB`) через 1 024, отдельно от представления UI. Полученные из округлённой строки bytes/rates обозначаются approximate; нельзя показывать ложную точность raw Engine counters. Decimal separator разбирается invariant, неизвестная unit — unavailable. CPU aggregate — сумма доступных процентов без деления на число контейнеров; memory/I/O sum требует одинаковой семантики и всех ожидаемых измерений либо явного partial. Missing denominator запрещает процент; stale values не смешиваются со свежими в якобы полном aggregate.

Logs запрашиваются только для verified owned IDs: ограниченный `--tail`, `--timestamps`, затем `--follow`/`--since` для продолжения. `--details` не применяется, чтобы не выводить лишние env/labels. Tail process принадлежит observation session, а не контейнеру: disposal завершает только чтение/клиент, не runtime. Redactor знает relevant secret set весь lifetime session, включая окончания Start; обрабатывает границы chunks/строк и escaped формы. Если secret set нельзя безопасно подготовить, вывод не открывается. Diagnostic errors не сохраняют raw HTTP bodies или private Compose/env contents.

| Удерживаемый ресурс | Жёсткий предел и поведение |
| --- | --- |
| Logs одной active observation | 10 000 строк и 8 MiB, действует первый достигнутый предел; старые записи удаляются с visible dropped/truncated counter |
| Logs приложения в памяти | 32 MiB суммарно, включая bounded ingestion/display queues; overflow не создаёт скрытого unbounded backlog |
| Одна log line | 16 KiB retained UTF-8; превышение обозначается, остаток строки не накапливается бесконечно; redaction выполняется до выдачи retained bytes |
| Metrics | До 300 samples/component, период 2 s, ring buffer; отсутствующие samples видимы |
| SeedPack preview | ≤100 records и ≤256 KiB/page; record ≤64 KiB; cache ≤8 MiB. Cursor продолжает по проверенной immutable section, усечение явно показано |
| Diagnostic disk logs | 5 файлов по 4 MiB после redaction; rollover без raw временного файла |
| Terminal operation history | Новейшие 1 000 globally; unresolved operations, tombstones и current-generation success markers не удаляются этим retention |

Все объёмы MiB/KiB — binary, 1 MiB = 1 048 576 bytes, 1 KiB = 1 024 bytes. Один active Inspect session: при переключении старый cancel/await/dispose завершается прежде нового; результаты дополнительно проверяют session/environment ID до UI update. Список использует общий bounded polling, не отдельный бесконечный tail каждого посещённого окружения. Логи и измерения не смешиваются между компонентами/окружениями; UI показывает pause, stale, truncation и продолжение просмотра.

## 10. Проверяемые инварианты

Contract fixtures/fault injection должны доказать: offline preview не запускает процессы/HTTP; source/image delta отклонён до первого runtime effect; staging не подменяется исходной папкой; pinned launch не следует изменённому tag; foreign/shared volumes не удаляются; одинаковые display names и два definition instances не смешиваются; отказ Stop блокирует разрушительные команды; partial edit не публикует revision; successful Start сохраняет данные; failure Start оставляет работающие компоненты; refresh success подтверждает Stop; cancel/crash не автоматически повторяет unsafe HTTP/destruction; safe close ждёт durable outcome; secret redaction проходит до каждого sink; sustained logs/100 переключений соблюдают все bounds и не сохраняют прежние subscriptions. Команды и live qualification — [quickstart.md](../quickstart.md).

Внешняя семантика, на которую опирается контракт: [Docker CLI](https://docs.docker.com/reference/cli/docker/), [Compose up](https://docs.docker.com/reference/cli/docker/compose/up/), [startup readiness](https://docs.docker.com/compose/how-tos/startup-order/), [labels](https://docs.docker.com/engine/manage-resources/labels/), [volumes](https://docs.docker.com/reference/compose-file/volumes/), [stats](https://docs.docker.com/reference/cli/docker/container/stats/), [logs](https://docs.docker.com/reference/cli/docker/container/logs/), [HTTP semantics](https://www.rfc-editor.org/rfc/rfc9110.html). Версии и rationale приведены в [research.md](../research.md); policy приложения не подменяется обещанием transaction/rollback со стороны Docker.
