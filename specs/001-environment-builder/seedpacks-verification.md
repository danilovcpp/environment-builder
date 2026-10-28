# Проверка Phase 9 / US7 — библиотека SeedPack

Дата: 2026-10-28. Выполнены только T110–T119. Следующая фаза — Phase 10 / US8; её задачи не выполнялись.

## Реализованное поведение

Библиотека импортирует внешний ZIP через существующий строгий importer. Точная версия появляется только после полной проверки manifest, состава, фактических hashes/размеров/counts, NDJSON и initializer compatibility. Повторная упаковка с другим сжатием не меняет identity; конфликт версии отклоняется. Импорт не изменяет сохранённые окружения и не имеет runtime/HTTP collaborators.

Preview читает проверенный blob по derived byte-offset index. Страница содержит максимум 100 записей или 256 КиБ, запись — максимум 64 КиБ, retained cache — максимум 8 МиБ с eviction. Курсор связан с точными pack/section; видны продолжение и конец секции. Утраченный индекс перестраивается. Текст отображается как текст, без исполнения разметки. Отмена и смена выбора не публикуют поздний результат прежнего запроса.

Удаление повторно проверяет saved/candidate/operation references внутри transaction и перечисляет владельцев. Активные payload leases блокируют удаление. Ссылки entries освобождаются атомарно; общие blobs сохраняются, неиспользуемое содержимое собирается только внутри private directory. Разрешённые terminal операции освобождают package references.

Выбор пакета адресуется конкретному instance ID, exact id/version/contentHash и объявленному initializer. Проверяются target contract, endpoints, sections и readiness; mandatory initialization сохраняется. Это изменение draft: сохранённое окружение обновляется существующим EditEnvironmentService с обычными guards/подтверждением. Импорт новой версии не заменяет прежний выбор.

Для Started/Unknown/Failed доступны явные решения. Независимо проверенный пользователем результат можно признать успешным после свежей read-only проверки ресурсов и generation. Состояние контейнера само не доказывает бизнес-результат. Осознанный повтор сохраняет полный idempotency key и лишь разрешает последующий отдельный Start. Новый refresh требует stopped state, полного preflight и нового точного destructive confirmation. Неопределённые Docker effects этим workflow не разрешаются. Решение, audit и закрытие разрешённой history фиксируются атомарно; потеря подтверждения commit сверяется по durable operation без повторного HTTP. Inspect показывает версию пакета и результаты шагов.

## Проверки

- `dotnet restore EnvironmentBuilder.sln --locked-mode -m:1 -nodeReuse:false` — успешно, NuGet audit включён, dependencies/locks не менялись.
- `dotnet build EnvironmentBuilder.sln -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false -warnaserror` — успешно, 0 warnings/errors.
- Unit — **109/109**, без skipped.
- Полная offline integration suite — **271/271**, без skipped; 25 новых cases US7 сверх исходных 246.
- UI `Category=UI` — **3/3**, без skipped. Общий STA harness дополнен production SeedPack importer/store/index/preview, offline assignment/save, reference-held delete, paging с настоящим keyboard input, Light/Dark и Unknown dialog. Binding/resource errors отсутствуют.
- Targeted `dotnet format ... --verify-no-changes --no-restore --include <изменённые C# файлы>` и `git diff --check` — успешно.

Общий `dotnet format EnvironmentBuilder.sln --verify-no-changes --no-restore` обнаружил прежние WHITESPACE/ENDOFLINE нарушения пяти неизменённых файлов: `EnvironmentSummary.xaml.cs`, `SeedPackDetails.xaml.cs`, `SeedPackPresentation.cs`, `SettingsDetails.xaml.cs`, `ShellViewModel.cs`. Эти файлы отсутствуют в итоговом diff. Общая проверка не объявляется пройденной; gate изменённых файлов пройден.

Первый параллельный sandbox build не завершился из-за ограничений MSBuild; проверки повторены с `-m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Sandbox запрещает настройку ACL private stores; integration/UI и format запущены с расширенными правами. Production ACL не ослаблены. Первый новый UI harness выявил отсутствие Dispatcher SynchronizationContext в тестовом STA; это исправлено. Финальные результаты находятся отдельно от промежуточных неуспешных прогонов.

Evidence: `artifacts/acceptance/us7-seedpacks/reports/seedpacks.json`; TRX — `verified/unit/unit-final.trx`, `verified/integration/integration-final.trx`, `verified/ui-final/ui-final.trx`. SC-009: первая страница 100 000-record fixture — **20,66 мс**; импорт и построение индекса не входят в замер. Проверены byte/record/cache bounds, continuation, eviction и восстановление индекса. SC-011 здесь подтверждён в scope US7: offline import/preview без runtime mutations/init HTTP, безопасный текстовый вывод и sanitized ошибки. Полная release security/performance matrix остаётся последующим задачам.

## UI и границы приёмки

По уточнению пользователя существующая компоновка Phase 7a сохранена: верхние команды, поиск и фильтры, четыре сводных блока, компактная таблица с иконками/бейджами, карточки секций, вкладки и правая контекстная панель. Workflows встроены в эти области. Демонстрационные сущности заменены секциями из manifest в прежних шаблонах карточек и строк; счётчики показывают измеренные данные. Preview остаётся справа. Функциональность создания пакета вне scope — импортируется внешний ZIP.

Просмотрены итоговые Light/Dark PNG при 1280×720 и 1672×941 DIP, фактический DPI 144 (150%), и сопоставлены с `docs/design/mockups/seed_packs.png`. Проверены production empty/selected/referenced states, paging controls, точная identity/owners на вкладке validation и Unknown dialog с default focus на отмене. При 1280×720 карточки и параметры доступны через прежний scroll; bounded текстовый preview имеет собственные scrollbars. Фиксированные примеры доменных записей заменены реальным текстом пакета в той же правой панели. Системный масштаб не изменялся; полная DPI/accessibility matrix T158 не закрывается.

Снимки: `artifacts/acceptance/us7-seedpacks/verified/ui-screenshots/seedpacks-production-*`, `seedpacks-unknown-*`; фактические размеры/DPI — `actual-dpi.txt` в том же каталоге.

Реальный Docker Engine и пользовательское хранилище не использовались. Применение и отказные сценарии проверены на независимых stores с Docker/HTTP doubles. Общий startup recovery и остальные истории остаются следующими фазами.

`.specify/extensions.yml` отсутствует до и после реализации; pre/post hooks не зарегистрированы. Checklist markers не изменялись.
