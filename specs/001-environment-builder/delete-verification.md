# Проверка Phase 8 / US6 — удаление окружения

Дата: 2026-10-28. Scope: только T103–T109. Phase 9 и последующие фазы не выполнялись.

Удаление подтверждает точный план с именем и UUID окружения, Engine/endpoint, resource UUID/actual IDs, consumers и последствиями потери данных. Для работающих контейнеров тот же workflow сначала выполняет verified Stop. Общий cleanup проверяет принадлежность и отсутствие каждого ресурса; уже отсутствующие ресурсы не получают повторной destructive команды.

Подтверждение и видимое состояние tombstone сохраняются одной SQLite transaction. При сбое или отмене authoritative snapshot, план, ledger, package references и незавершённый journal сохраняются. Повтор получает свежий plan/confirmation и не может сменить Engine, generation или расширить scope. Финализация требует durable absence proofs и атомарно освобождает references/ledger/data records, закрывает описание и сворачивает разрешённую history в summary без resource/blob references. Минимальная closed environment row сохраняется для history FK; в live list она отсутствует. Чужая unresolved history не удаляется cascade. Поздняя отмена после atomic commit не переписывает summary.

## Автоматические проверки

- `dotnet restore EnvironmentBuilder.sln --locked-mode -m:1 -nodeReuse:false` — успешно, штатный NuGet audit включён; зависимости и lock files не менялись.
- `dotnet build EnvironmentBuilder.sln --configuration Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false -warnaserror` — успешно, 0 warnings/errors.
- Unit suite — 106/106, включая 6 delete VM/confirmation cases, без skipped.
- Полная offline integration suite — 246/246, включая 28 delete contract/isolation/fault cases, без skipped.
- UI suite `Category=UI` — 3/3; общий STA harness дополнен delete/retry/remnants/confirmation и keyboard focus в обеих темах; binding/resource errors отсутствуют.
- `dotnet format ... --verify-no-changes --no-restore --include <все изменённые C# файлы>` и `git diff --check` — успешно.

Корень evidence: `artifacts/acceptance/us6-delete/`. Итоговые TRX: `verified/unit/unit-final.trx`, `verified/integration-final/integration-final.trx`, `verified/ui/ui-final.trx`.

Sandbox запрещает изменение ACL приватных тестовых stores и доступ к локальному MSBuild named pipe. Integration/UI/format были запущены с расширенными правами; production ACL не ослаблены. Locked restore повторён с доступом к NuGet после sandbox NU1900. Изолированные тесты не обращались к реальному Docker Engine или пользовательскому store.

Полный `dotnet format EnvironmentBuilder.sln --verify-no-changes --no-restore` выявил существующие WHITESPACE/ENDOFLINE нарушения в пяти неизменённых файлах: `EnvironmentSummary.xaml.cs`, `SeedPackDetails.xaml.cs`, `SeedPackPresentation.cs`, `SettingsDetails.xaml.cs`, `ShellViewModel.cs`. Их отсутствие в diff подтверждено. Это не объявляется пройденной общей проверкой; targeted formatting gate данной фазы пройден.

## Independent fixture и evidence

`artifacts/acceptance/us6-delete/reports/delete.json` создаётся интеграционным сценарием при установленном `ENVIRONMENTBUILDER_ACCEPTANCE_ARTIFACTS`. Сценарий использует реальную SQLite persistence, production DockerStopSession/DockerCleanupSession и stateful CLI double: работающая выбранная копия, вторая работающая копия, foreign lookalike, source file, отказ удаления volume, видимая tombstone и повтор после нового confirmation.

Зафиксированы exact mutation commands, plan/resource/operation IDs, deleted/remnants до повтора и проверенное отсутствие после него. Итог: scope из трёх ресурсов полностью удалён, ledger пуст; вторая копия/foreign/source неизменны; global cleanup, image и source mutations — 0. Failure suites дополнительно проверяют copied labels, stopped foreign consumers, network attachments, unknown Stop, потерю result commit после каждого cleanup stage, отмену между stages, atomic finalization/lost acknowledgement и удержание references при отказе.

## Визуальная проверка

Просмотрены `verified/ui-screenshots/delete-remnants-{light,dark}.png` и `delete-confirmation-{light,dark}.png`. Окно списка — 1280×720 DIP, диалог — 780×660 DIP; фактический DPI 144×144 (150%), записан в `actual-dpi.txt`. Проверены длинное имя, UUID, deleted/remnants/retry reason, disabled start/edit/data actions, доступный retry, предупреждение потери данных и фокус на отмене в диалоге.

Сравнение с `docs/design/mockups/seed_packs.png`: сохранены shell/table/card geometry, semantic palette, typography и shared controls Phase 7a. Дополнительная область остатков над таблицей и перенос destructive action на следующую строку при 1280 DIP нужны для видимости последствий удаления; кнопка доступна, clipping отсутствует. Полная release DPI-матрица и ручные SC других историй остаются в следующих фазах.

`.specify/extensions.yml` отсутствует до и после реализации; pre/post hooks не зарегистрированы.
