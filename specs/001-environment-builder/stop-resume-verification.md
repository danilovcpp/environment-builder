# Проверка Phase 5 / US3 — Stop и повторный Start

Дата: 2026-10-27 (Asia/Omsk). Область: только задачи T078–T084. Фазы 6+ не выполнялись. Область проверки уточнена 2026-10-28.

Статус фазы: реализация и автоматические проверки завершены. Runtime проверяется с Docker/HTTP doubles; запуск внешних сервисов для checkpoint не требуется.

## Изменения

- `QuiescenceGuard` принимает свежую подтверждённую остановку или проверенный пустой состав. Неизвестное/устаревшее наблюдение, running/paused/restarting/dead, конфликт владения или незавершённая конфликтующая операция блокируют guard с причиной.
- Stop выполняется внутри существующей сериализации по environment ID. До эффекта сохраняется intent, повторно проверяются Engine и exact ownership; контейнеры останавливаются по ID в обратном порядке зависимостей, включая owned-остатки из ledger. После этапов перечитывается полный состав. Удаление ресурсов, изменение specification/revision/generation и success markers не выполняются.
- Graceful timeout входит в immutable operation plan. Наблюдённые exit code/OOM сохраняются в результате шага и доступны в Inspect. Код 137 сам по себе не объявляется доказательством достижения timeout. Семантика CLI сверена с [Docker stop](https://docs.docker.com/reference/cli/docker/container/stop/).
- При повторном Start существующие контейнеры запускаются по exact ID через [Docker start](https://docs.docker.com/reference/cli/docker/container/start/). Проверяются активное поколение и incarnation томов перед запуском, чтением прежних success markers и фиксацией нового HTTP-успеха. Отсутствующий/заменённый/неподтверждённый том инвалидирует прежнюю готовность; Inspect показывает blocker и действие для восстановления.
- Inspect получил Stop, причины недоступности и отдельное подтверждение quiescence. Обновление наблюдения остаётся самостоятельной командой. Явный Stop разрешён после завершившегося с NeedsAttention Start; он не снимает прежнюю необходимость recovery и не разрешает destructive commands автоматически.

## Задачи

| Задачи | Статус | Подтверждение |
| --- | --- | --- |
| T078, T079 | [X] | Контрактные и Core/VM проверки включены в успешные suites |
| T081–T083 | [X] | Реализация, regression tests, Release/analyzers и UI проверки |
| T084 | [X] | Commands/DI/Inspect подключены и проверены integration/UI suites |

Контрактные тесты проверяют цепочку из трёх зависимых компонентов и owned-остаток, exact IDs, timeout, сохранность ledger/specification/generation/success tuple/attempt history. Отдельно покрыты потеря связи, неизвестное/повторно запущенное состояние, скопированные labels, ресурс вне ledger, смена Engine, клиентский timeout, отказ durable intent/result. Unit/VM tests проверяют guard, повторное использование generation/tuple, invalidation перед повтором или после HTTP dispatch, доступность Stop и независимость Refresh.

## Результаты

| Проверка | Результат |
| --- | --- |
| `dotnet restore EnvironmentBuilder.sln --locked-mode --disable-parallel -m:1 -nr:false` | Passed |
| Release build всего solution, analyzers/warnings as errors | Passed, 0 warnings / 0 errors |
| Unit/VM | Passed, 82/82, skipped 0 |
| Offline integration | Passed, 146/146, skipped 0 |
| UI/FlaUI (`Category=UI`) | Passed, 3/3, skipped 0 |
| `dotnet format EnvironmentBuilder.sln --verify-no-changes --no-restore` | Passed |
| `git diff --check` | Passed |

Сборка: `dotnet build EnvironmentBuilder.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`. Shared compilation отключена только для запуска tooling в данной среде; настройки проекта не менялись. Offline integration/UI/format запущены с разрешением доступа к Windows ACL/desktop/named pipes после ограничений песочницы.

UI проверка включает keyboard smoke, выключенную Stop без подтверждённых работающих контейнеров, Light/Dark и отсутствие binding/resource errors. PNG обеих тем Inspect просмотрены. Полная release/DPI/soak matrix в этой фазе не заявляется выполненной.

## Evidence

- [Unit TRX](../../artifacts/us3/final/unit/unit.trx), [offline integration TRX](../../artifacts/us3/final/integration-offline/offline.trx), [UI TRX](../../artifacts/us3/final/ui/ui.trx).
- [Inspect Light](../../artifacts/us3/final/ui/resources-inspect-light.png), [Inspect Dark](../../artifacts/us3/final/ui/resources-inspect-dark.png).
- [Build log](../../artifacts/us3-build.log), [restore log](../../artifacts/us3-restore.log), [format verification log](../../artifacts/us3-format-verify.log).

`.specify/extensions.yml` отсутствует: pre/post implementation hooks не зарегистрированы. Checklist requirements.md прочитан без изменения маркеров: 16/16, unchecked 0.
