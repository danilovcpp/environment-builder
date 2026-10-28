# UI contract v1

**Status**: проектный контракт основной версии; не результат проверки реализованного UI.

Источники: [spec.md](../spec.md), [plan.md](../plan.md), [data model](../data-model.md), [runtime contract](runtime.md), [style guide](../../../docs/design/style-guide.md). Спецификация определяет поведение; макеты обязательны как визуальные референсы. По уточнению пользователя от 2026-10-28 основной эталон при неточностях layout, оформления и цветов — [seed_packs.png](../../../docs/design/mockups/seed_packs.png), включая расхождения с прежними значениями style guide.

## 1. Shell и navigation

- Постоянный shell содержит левую navigation, PageHeader, content, сворачиваемую правую ContextPanel и нижний StatusBar. Общие компоненты и semantic resources переиспользуются всеми экранами.
- Верхний уровень: «Окружения», «Тестовые данные», «Настройки», «Диагностика». Начальный маршрут — список окружений.
- Создание, редактирование и Inspect вложены в «Окружения». Compose preview открывается в контексте текущего editor/environment, сохраняет контекст возврата и отображаемую ревизию.
- Навигационный адрес содержит immutable environment ID, а для нового draft — собственный draft ID. Имя не служит identity. Два одинаковых имени различаются ID и компонентами.
- Search, filters, expanded rows, selected ID и scroll position принадлежат состоянию списка. Возврат после save/cancel/Inspect восстанавливает их, если элемент существует; удалённое выделение снимается. Успешный create выделяет новое окружение.
- Loading, исходно пустой список, пустой search result, ошибка чтения отдельного документа, общая ошибка хранения и Docker unavailable — разные состояния с доступным следующим действием. Один повреждённый документ не скрывает остальные.
- PageHeader содержит название, описание и ведущую команду; технические детали операций не подменяют пользовательское назначение действия. StatusBar показывает Docker connectivity/freshness и число активных операций; полный прогресс доступен после смены страницы.

## 2. Observable state и async boundaries

ViewModel владеет состоянием экрана, validation и командами. View-only механика focus, visual tree, clipboard/dialog host и window events остаётся во View/малой UI boundary. ViewModels не получают Window, Page, MessageBox или конкретный control.

Каждая команда с внешней работой немедленно показывает принятие, loading и operation ID. Неизвестная доля выполнения показывается именем этапа и indeterminate indicator. Успех, отмена, failure, partial и unknown outcome различаются; exception без UI результата недопустим.

App-owned operation coordinator переживает navigation и блокирует конфликтующие изменения одного environment ID со всех экранов. Отключение локальной кнопки не заменяет эту проверку. Разные окружения могут выполнять независимые операции. Повтор той же команды показывает существующую операцию, а не создаёт конкурирующее изменение.

У screen/observation session есть lifetime и monotonically increasing session ID. При смене окружения предыдущие log/metric subscriptions отменяются, ожидаются и освобождаются до активации новых. Поздний ответ со старым session ID не меняет текущие значения. Screen cancellation не отменяет runtime operation. Все bound collections обновляются на Dispatcher короткими пакетами; I/O и SQLite worker не выполняются на UI thread.

## 3. Редактор и validation

- ID существующего окружения доступен для чтения/копирования, но не редактируется. Draft содержит имя, необязательное описание, экземпляры компонентов, sources, effective parameters, dependencies, ports, storage и baseline.
- Каждому экземпляру назначается собственный immutable ID; definition может повторяться. Dependencies и baseline адресуют instance ID. Автоматическое включение зависимости содержит причину. Если кандидатов несколько, нужен явный выбор; интерфейс не выбирает случайный экземпляр.
- Target source — image или local build folder; infrastructure source — только image. Выбор папки не исполняет содержимое. Компонентная библиотека допускает создание формой и импорт неизвестного declarative definition без сервисного имени в приложении.
- Библиотека редактирует имя, источник, иконку, цвет; только для целевых сервисов — зависимости из уже добавленных компонентов с признаком обязательности. Порты, env, хранилища и параметры сборки задаются при создании/редактировании среды. Обязательные зависимости добавляются автоматически, необязательные включаются отдельно. При вводе инфраструктурного образа выполняется поиск Docker Hub с отменой устаревших запросов; выбор загружает страницы тегов и логотип при наличии. Ручной ввод образа/тега доступен при ошибке сети; отсутствие логотипа оставляет выбранную иконку.
- Каждый published port по умолчанию ограничен текущим компьютером; явное включение network access относится только к выбранной записи и видно в preview. Control port для инициализации, если нужен, также явно виден и остаётся loopback.
- Structural validation привязывает ошибку к полю/instance/edge и блокирует Save: пустое имя, пустой состав, неоднозначная/отсутствующая dependency, cycle, конфликт port mapping, незаданный required parameter, неверная форма документа.
- Missing local folder/image/secret/SeedPack и Docker unavailable показаны как readiness issues. Структурно корректный draft сохраняется; известная недоступность input блокирует affected execution. Неизвестная image identity после offline preview допускает вход в Start preflight: он проверяет локальный образ/реестр и возможность pull, показывает delta и получает подтверждение до runtime mutation. Unknown не выдаётся за установленную неисправность и не создаёт тупик, где для проверки нужен уже разрешённый Start.
- Сохранённые effective settings являются snapshot; изменения defaults/definition не меняют их. Применение новых defaults к существующему окружению — явное редактирование с обычными последствиями сохранения.
- Save нового draft создаёт ревизию 1 без build, containers или initialization. Save без изменений не увеличивает revision и не запускает cleanup.

## 4. Command availability

Любая недоступная команда имеет читаемую причину, доступную с клавиатуры. Unsupported component action не предлагается как исполнимая. Возможность подтверждена coordinator повторно при выполнении.

| Команда | Условия и видимый результат |
| --- | --- |
| Create / save new | Docker не требуется; structural validation пройдена; после save только persisted description |
| Edit / save changed | Подтверждён `ConfirmedQuiescent`, включая проверенное отсутствие ресурсов ещё не запускавшегося окружения; конфликтующей операции нет; Save показывает destructive plan даже при изменении только имени |
| Start | Structural validity и совместимый Docker допускают preflight для unknown image; launch допускается после разрешения всех inputs; input delta после preview требует отдельного подтверждения до runtime mutation |
| Stop | Runtime допускает адресную остановку и нет конфликта; данные/initialization-success сохраняются; успех только после подтверждения stopped |
| Обновить состояние | Только перечитать observation; не менять settings, resources или business data; недоступный Docker даёт unknown/stale |
| Очистить данные | Подтверждённый stopped и доказанное владение; после подтверждения удалить перечисленные persisted данные и initialization marks, сохранить spec/revision/baseline |
| Восстановить baseline (refresh данных) | Подтверждённый stopped, весь baseline preflight пройден и способ применения известен; план показывает временные Start/Stop; успешный итог stopped |
| Delete | Подтверждённый план stop/cleanup/delete, ownership и consumers проверены; запись исчезает только после полного успеха, иначе доступна с остатками |
| Logs / endpoints / status | Поддерживаемое наблюдение конкретного instance; missing/unknown объясняется; дополнительные индивидуальные runtime-команды не обязательны |
| Import / export / SeedPack preview | Только parsing/storage; не создавать контейнеры, процессы или initialization HTTP; missing local dependencies перечислить без подстановки |
| Diagnostics fix | Отдельная явная команда с описанием последствий; открытие Diagnostics само ничего не исправляет |

`ConfirmedQuiescent` означает фактически проверенное отсутствие running/restarting/paused/unknown owned containers и конфликтующей операции, включая verified empty resource set. Это guard редактирования/данных; отображаемый статус NeverStarted остаётся отличимым от ранее Stopped. Недоступный Docker не доказывает quiescence.

Partial Start или ошибка initialization оставляют уже работающие компоненты доступными в Inspect до явного Stop/Delete. UI не предлагает ложный completed/ready и не обещает rollback.

## 5. Подтверждения, stale plans и dirty draft

При уходе из изменённого редактора modal содержит «Сохранить», «Не сохранять», «Остаться». Escape и закрытие modal эквивалентны «Остаться». Выбор «Сохранить» применяет обычную validation и, для существующего окружения, destructive confirmation; при ошибке переход не выполняется. «Остаться» сохраняет введённые значения и focus. Отмена более позднего destructive confirmation также оставляет draft.

Destructive confirmation содержит environment name + ID, действующую revision, точные resources/data, неизбежную потерю данных, planned temporary runtime transitions и причину операции. У destructive действия отдельный доступный текст; цвет не является единственным предупреждением. Cancel не меняет persisted spec, revision или runtime/data.

Подтверждение связано с plan ID/fingerprint. Перед выполнением coordinator проверяет состав плана, stopped state, ownership и foreign consumers. Существенно изменённый план показывается заново; старое подтверждение не используется. Unknown ownership, неподтверждённая остановка и совместное использование блокируют affected deletion. Partial failure показывает уже выполненные действия, остатки и безопасный следующий шаг; deleted data не объявляются восстановимыми.

Изменение source/image/прочих проверяемых inputs после preview имеет отдельный delta dialog с прежним/новым состоянием и «Продолжить»/«Отмена». Unknown→resolved также отражается. Отмена заканчивает запуск до изменения runtime ресурсов/данных.

## 6. Обычное закрытие окна

Window Closing передаётся в ShutdownCoordinator и временно отменяется view-specific bridge. При активных операциях modal предлагает «Дождаться и закрыть», «Безопасно отменить и закрыть» и «Вернуться в приложение». Ни один вариант не скрывает окно с продолжающимися фоновыми операциями.

- Wait блокирует новые изменяющие операции и сохраняет окно/прогресс до завершения всех активных работ и записи результатов.
- Cancel запрашивает остановку дальнейшего scheduling и ожидает safe points, reconciliation и persistence результатов. Текущий noncancellable этап показывает причину ожидания; процесс не завершается по UI-таймауту.
- Return снимает pending close и оставляет операции работать. Недопустимо отождествлять отмену с rollback или полным успехом.
- Dirty draft рассматривается до окончательного выхода тем же трёхвариантным диалогом; «Остаться» отменяет закрытие. Решения о draft и operations не теряются при переключении modal.
- После безопасного завершения освобождаются subscriptions/scopes/provider и выполняется единственный окончательный Shutdown. `async void` допустим только у framework event bridge; ожидаемая работа имеет Task и observable result.

Принудительное завершение процесса — отдельный recovery сценарий: после запуска показываются незавершённая операция и результат reconciliation, без автоматического повтора destructive/небезопасных initialization steps.

## 7. Inspect, SeedPack preview и границы данных

Inspect различает configured/factual component, Docker status/health, no healthcheck, unavailable measurement, measured zero, stale value и partial aggregate. Отображает timestamps и freshness; недоступный Docker не превращается в stopped. Ports/endpoints относятся к выбранному экземпляру и известному фактическому mapping.

Предельные значения UI согласованы с [runtime contract](runtime.md):

| Поток | Предел / поведение |
| --- | --- |
| Logs | 10 000 строк или 8 MiB на active observation, что наступит раньше; общий retained budget приложения 32 MiB; строка максимум 16 KiB |
| Metrics | 300 samples/component с интервалом 2 s; eviction старых samples; missing/stale отдельно от нуля |
| SeedPack preview | 100 records или 256 KiB/page; record максимум 64 KiB; cache максимум 8 MiB; continuation и ограниченность просмотра видны |

Ограниченные очереди, retention/drop counters и markers делают усечение наблюдаемым. При переполнении UI продолжает показывать новые данные; navigation не ждёт освобождения неограниченного backlog. Preview работает без контейнеров и не читает весь пакет в memory. Text preview не интерпретирует записи как XAML/HTML/команды. Доменные секции возникают из содержимого пакета, не из фиксированных экранов приложения.

## 8. Secrets и errors

Preview/export используют dedicated sanitized representations со ссылками/placeholders вместо значений. Known secrets и объявленные sensitive fields маскируются до помещения в bound state, log ring, diagnostics или persistent sink, включая escaped forms и chunk boundaries. VM не хранит raw process/HTTP response bodies для отложенной маскировки.

Если relevant secret set не подготовлен, affected observation не открывается и показывает причину. Начало tail не зависит от сохранения plaintext после Start; lifetime redaction context принадлежит observation. Errors показывают component/stage/actionable cause, но не secret values, request/response bodies или raw command environment. Проверка отсутствия секретов охватывает также dialogs, clipboard/export и сохранённые diagnostics.

## 9. Темы, accessibility и visual acceptance

Следующий этап после US5 — Phase 7a / T166–T177 из [tasks.md](../tasks.md#phase-7a-ui-refinement), до реализации US6. Общая компоновка shell, поверхности, таблицы, фильтры, кнопки, иконки, бейджи и контекстные сводки нормализуются по `seed_packs.png`; остальные референсы задают области своих экранов. Светлая тема воспроизводит основной эталон; тёмная сохраняет его layout с semantic Dark tokens. Поведение по спецификации и доступность обязательны, обусловленные ими визуальные отклонения документируются.

Приёмка требует просмотра и сравнения screenshots до/после с референсами при сопоставимых window size/DPI и отчёта по каждому экрану. Одного прохождения resource/binding/smoke tests недостаточно. Список окружений остаётся компактной таблицей с раскрываемым составом, editor — структурированными областями компонентов и сводкой, Inspect/Compose — читаемыми рабочими областями в общем shell. Для ещё не завершённых US7/US8/US10 на Phase 7a готовятся представления с реальными empty/unavailable states; их полноценные workflows подключаются в соответствующих историях без повторного изобретения оформления. Fixture-данные используются только в тестовых хранилищах.

Shared Light/Dark token dictionaries реализуются до экранов. WPF UI resources загружаются перед repo overrides; system accent override отключён. Во Views/ControlTemplates нет собственных palette HEX. Theme switch не пересоздаёт route/VM и сохраняет layout, draft, selection, scroll, active operation/progress.

Общий PageHeader/ContextPanel/StatusBar соответствует [style guide](../../../docs/design/style-guide.md); одна Lucide icon family. Значимый обычный текст — contrast ≥4.5:1, крупный текст/ключевая графика ≥3:1 на фактическом фоне и во всех используемых states. Light TextTertiary на Canvas не использовать для значимого текста: расчёт пары даёт около 4.39:1; существующий TextSecondary позволяет выполнить QR-002 без новой палитры.

Keyboard покрывает navigation, формы, таблицы, контекстные команды, preview paging и modal choices; focus виден и возвращается к инициатору dialog. Icon-only commands имеют accessible name и tooltip. Error summary ведёт к полю, validation не выражается только цветом. Long names доступны целиком через keyboard-accessible detail/tooltip, не перекрывая actions.

При 1280×720 DIP и 100/125/150/200% DPI обязательные поля/кнопки/modal доступны через layout или scroll; ContextPanel сворачивается, собственный scroll не уводит PageHeader/StatusBar. Это проверяется в обеих темах, вместе с Hover/Pressed/Selected/Focused/Disabled/error states, binding/resource errors и отсутствием новых подписок после navigation. Проверки и свидетельства — [quickstart](../quickstart.md).
