# Проверка Phase 7 / US5

Дата: 2026-10-28. Выполнены только T094–T102. Phase 8 и последующие фазы не выполнялись.

## Реализованное поведение

- «Обновить состояние» перечитывает наблюдение без изменения данных, конфигурации и ревизии.
- «Очистить данные» требует ConfirmedQuiescent и явного подтверждения точного плана всех принадлежащих окружению ресурсов. Общий cleanup удаляет освобождающие контейнеры, тома и private network с проверкой identity/consumers перед каждым эффектом и отсутствия после удаления.
- Одна SQLite transaction переводит прежнюю generation в retired, выделяет новую active generation без прежних success markers и очищает ledger. Сохранённые JSON, revision, baseline, summary indexes и environment package references не изменяются. Сбой до commit сохраняет прежнюю authoritative конфигурацию и invalidated generation; потерянное подтверждение commit проверяется по durable состоянию без повторного удаления.
- Refresh проверяет все inputs, immutable packs, compatibility и declared initializer entry до cleanup. Подготовленный session удерживает captured source/input snapshots, разрешённые image identities и secret context для новой generation. Подтверждаемый plan включает временные resource UUID, concrete components, published/control mappings и обязательную остановку.
- После cleanup refresh публикует новое поколение и продолжает в той же операции. Общий Start workflow проверяет dependencies/readiness, выполняет BaselineExecutor, затем проверяются durable successes всех steps. Успех требует verified Stop и повторной проверки сохранности generation/volumes после него. Empty и mandatory-only baseline поддерживаются.
- Ошибка/отмена инициализации не запускает автоматический Stop или rollback. Inspect сохраняет operation ID, этапы, удалённые ресурсы, остатки и фактический runtime; отдельный явный Stop разрешён при unresolved restoration.
- List/Inspect предоставляют разные команды и объяснения; доступность учитывает остановку, ownership, конфликты и inputs baseline. Общий destructive dialog показывает исходную ревизию без ложного обещания новой revision для reset.

## Автоматические проверки

| Gate | Результат |
| --- | --- |
| Locked restore, `dotnet restore EnvironmentBuilder.sln --locked-mode -m:1 -nodeReuse:false` | PASS |
| Release build, `--no-restore -warnaserror -m:1 -nodeReuse:false -p:UseSharedCompilation=false` | PASS, 0 warnings/errors |
| Unit/VM suite, Release `--no-build --no-restore` | PASS, 97/97 |
| Полная offline integration suite, без фильтра | PASS, 217/217 |
| WPF/FlaUI suite, `--filter Category=UI` | PASS, 3/3 |
| `dotnet format EnvironmentBuilder.sln --verify-no-changes --no-restore` | PASS |
| `git diff --check` | PASS |

TRX: `artifacts/us5/final/unit.trx`, `integration.trx`, `ui.trx`. Пропущенных тестов нет. SDK — 10.0.402; зависимости и lock files не менялись. Конфигурация ignore files проверена, существенных отсутствующих patterns нет.

Sandbox запрещает установку Windows ACL и доступ к named pipes build host. Integration/UI и format выполнены вне sandbox после соответствующего отказа; приватные ACL и desktop checks не отключались. В sandbox сборка работает с одним MSBuild node и выключенным shared compiler.

## SC-006 / SC-007 и faults

`DataResetScenarioTests` создаёт отдельный изолированный store для каждого из 10 clear и 10 refresh. Docker cleanup проходит через production DockerCleanupSession с контролируемыми list/inspect/removal responses. Temporary runtime и бизнес-данные представлены моделью ресурсов; HTTP исполняется production HttpInitializationTransport через FakeHttpMessageHandler, журнал — настоящая SQLite.

Все 20 операций завершились Succeeded, без remnants, с actual stopped proof и прежней revision. Clear дал count 0 и 0 initialization sends. Refresh дал ожидаемый count 5: mandatory initialization и две записи выбранного pack, два HTTP-запроса, без дублей. Старые markers retired generation не удостоверяют готовность новой; второй saved snapshot, работающая контрольная копия и foreign lookalike сохранены.

Отчёт: `artifacts/acceptance/us5-20261028/reports/data-reset.json`. Воспроизведение: перед полной integration suite задать `ENVIRONMENTBUILDER_ACCEPTANCE_ARTIFACTS` абсолютным путём к желаемому каталогу reports. Без переменной все assertions выполняются, файл не создаётся.

Дополнительные cases проверяют missing pack/provider/secret/corrupt blob/undeclared apply contract до подтверждения и удаления; mandatory-only/empty baseline; cancel/stale token/foreign consumer; generation-invalidation/intent/result/finalization faults; потерю commit acknowledgement; отмену после каждого cleanup effect; ошибку и отмену images/start/readiness/HTTP/Stop; unknown final Stop и потерю volume на последнем этапе. Неудачная инициализация сохраняет running components до отдельного явного Stop.

## UI и границы подтверждения

Light/Dark screenshots Inspect, clear и refresh dialogs просмотрены в `artifacts/us5/final/resources-*.png`. Проверены разные labels, disabled commands при неподтверждённой остановке, неизменность контекста при theme switch, отсутствие binding/resource errors и Escape/cancel общего диалога. Таблица и логи Inspect сохраняют видимую область при окне 1280×720.

Полная матрица DPI и пяти пользовательских объяснений SC-012 здесь не заявляется пройденной: она относится к последующей release приёмке. Runtime-сценарии проверены с doubles; реальные Docker containers и внешние сервисы не запускались, как предусмотрено test contracts фазы.

`.specify/extensions.yml` отсутствует до и после реализации; hooks не зарегистрированы.
