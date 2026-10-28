# Проверка Phase 6 / US4 — изменение остановленного окружения

Дата: 2026-10-28 (Asia/Omsk). Выполнена только Phase 6, T085–T093. Фазы 7+ не выполнялись.

## Поведение

- Существующее описание открывается для просмотра. «Изменить окружение» проверяет свежую quiescence, включая verified empty scope никогда не запускавшегося окружения. Сохранение повторяет проверки внутри сериализации по environment ID. ID окружения и сохраняемых экземпляров не переназначаются.
- Canonical no-op не создаёт операцию, не запрашивает подтверждения, не обращается к Docker и не увеличивает ревизию. Реальное изменение, включая только имя или пустой scope, требует явного подтверждения immutable плана.
- План содержит прежнее имя/ID/revision, candidate revision, Engine/endpoint, observation time, исходное и будущее поколения, точные ресурсы/labels/incarnation/consumers, fingerprints кандидата, этапы и предупреждение о потере данных. Изменение существенных условий отклоняет старое подтверждение.
- До первого destructive attempt прежняя generation durable инвалидируется. Удаление выполняется только по exact identities: stopped containers → verified local volumes → private bridge network. Перед каждым эффектом заново проверяются authority, specification, ledger, quiescence и все consumers, включая stopped containers и network attachments. Force/global cleanup не используется; images, source и import files в scope не входят.
- Intent фиксируется до вызова Docker; после каждого этапа сохраняется проверенное отсутствие. Already absent resources получают durable evidence без повторной команды удаления. Частичный результат сохраняет прежнюю authoritative specification, candidate, подтверждённо удалённое и остатки; удалённые данные не восстанавливаются.
- После полной проверенной очистки одна SQLite transaction публикует тот же environment ID с revision +1, fresh active generation без прежних success markers, summary indexes, package references и успешный результат операции. Сбой commit оставляет старый snapshot. Потеря acknowledgement после успешного commit разрешается чтением durable state. Явная финализация после новой сверки не повторяет cleanup и использует тот же lock окружения.
- Editor сохраняет черновик при отмене/ошибке, возвращает к same-ID item после успеха и поддерживает dirty Save. Новые правки, появившиеся во время commit, остаются dirty. Отмена сохранения записывается в журнал; после начала удаления возможен partial outcome.
- Новые необязательные поля плана не меняют hashes ранее записанных планов и подтверждений. External document schemas и DB schema version не менялись.

## Проверки

| Проверка | Результат |
| --- | --- |
| Locked restore всего solution | Passed |
| Release build, analyzers / warnings as errors | Passed, 0 warnings / 0 errors |
| Unit / ViewModels | Passed, 91/91, skipped 0 |
| Offline integration | Passed, 185/185, skipped 0 |
| UI / FlaUI, Category=UI | Passed, 3/3, skipped 0 |
| dotnet format --verify-no-changes --no-restore | Passed |
| git diff --check | Passed |

US4 добавляет 9 unit/VM и 42 integration contract/fault cases. SC-005 проверен на независимых prepared stores: name-only, settings и composition edits; same ID/+1/fresh generation; never-started empty scope; canonical no-op; cancel до удаления и после первого эффекта; stale plan с новым подтверждением; partial cleanup и persistence faults; явная финализация без повторного удаления. Дополнительно проверены refs/indexes, отсутствие ресурса вопреки успешному exit CLI, copied labels/incarnation, missing evidence, foreign stopped consumers/attachments, Engine/revision/ledger/state drift, две runtime-копии и foreign lookalike.

UI gate проверяет ресурсы обеих тем, точный план с предупреждением и прокруткой, исходный фокус отмены, реальный Escape, прежние authoring/Inspect keyboard scenarios и отсутствие binding/resource errors. Light/Dark PNG нового диалога просмотрены. Полная release/DPI/accessibility/soak matrix остаётся в Phase 13 и здесь не заявляется выполненной.

Команды:

```powershell
dotnet restore EnvironmentBuilder.sln --locked-mode --disable-parallel -m:1 -nr:false
dotnet build EnvironmentBuilder.sln -c Release --no-restore -m:1 /p:UseSharedCompilation=false /nodeReuse:false
dotnet test tests/EnvironmentBuilder.UnitTests/EnvironmentBuilder.UnitTests.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=unit.trx' --results-directory artifacts/us4/final/unit
dotnet test tests/EnvironmentBuilder.IntegrationTests/EnvironmentBuilder.IntegrationTests.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=offline.trx' --results-directory artifacts/us4/final/integration-offline
$env:ENVIRONMENTBUILDER_UI_ARTIFACTS = Join-Path $PWD 'artifacts/us4/final/ui'
dotnet test tests/EnvironmentBuilder.UiTests/EnvironmentBuilder.UiTests.csproj -c Release --no-build --no-restore --filter Category=UI --logger 'trx;LogFileName=ui.trx' --results-directory artifacts/us4/final/ui
dotnet format EnvironmentBuilder.sln --verify-no-changes --no-restore
git diff --check
```

Shared compilation отключена только для запуска tooling в этой среде. Integration, UI и format выполнены вне sandbox: Windows ACL приватных тестовых stores, desktop и MSBuild named pipes недоступны внутри sandbox. Docker/HTTP используются через doubles; внешние сервисы не запускались. Счётчики TRX проверены: suites непустые, все tests passed, skipped 0.

Evidence: [SC-005 / edit.json](../../artifacts/us4/final/reports/edit.json), [Unit TRX](../../artifacts/us4/final/unit/unit.trx), [Integration TRX](../../artifacts/us4/final/integration-offline/offline.trx), [UI TRX](../../artifacts/us4/final/ui/ui.trx), [Light PNG](../../artifacts/us4/final/ui/resources-destructive-edit-light.png), [Dark PNG](../../artifacts/us4/final/ui/resources-destructive-edit-dark.png).

Ignore files проверены; дополнения не потребовались. Checklist requirements.md: 16/16, unchecked 0; маркеры не изменялись. `.specify/extensions.yml` отсутствует, pre/post implementation hooks не зарегистрированы.
