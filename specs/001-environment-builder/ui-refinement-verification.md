# Проверка Phase 7a — UI по референсам

**Дата:** 2026-10-28. **Статус:** PASS в уточнённом пользователем scope.
Выполнены и отмечены T166–T177. Phase 8 и последующие фазы не выполнялись.

Переработаны общие темы, shell, таблица окружений, редактор, Inspect, Compose
и диалоги. Текстовые заглушки SeedPacks/Settings/Diagnostics заменены
подготовленными представлениями с реальными read-only данными. Полные workflows
T118/T127/T148 остаются незавершёнными.

Проверки: locked restore; Release build (0 warnings/errors); unit **100/100**,
offline integration **218/218**, UI/FlaUI **3/3**, без skip; format и diff check.
Исправлен переход validation summary к параметрам свёрнутого экземпляра.
Сохранены route/draft/selection/scroll/progress и существующие guards US1–US5.

Снимки Light/Dark при 1280×720 и 1672×941 DIP просмотрены и сопоставлены с
референсами. Фактический DPI **144 (150%)**. По указанию пользователя
«не надо проверять при других масштабах» дополнительные масштабы в этом запуске
не требуются; системный масштаб не менялся. Полная release-матрица T158 не закрыта.

Карта экранов, геометрия и обоснованные адаптации:
[ui-reference.md](../../docs/design/ui-reference.md).
Полное локальное evidence: [verification.md](../../artifacts/ui-refinement/20261028/verification.md),
[reference-review.md](../../artifacts/ui-refinement/20261028/reference-review.md),
[сравнение изображений](../../artifacts/ui-refinement/20261028/comparison.html).
TRX, build output и actual DPI находятся в том же каталоге artifacts.

Runtime contracts и переносимые форматы не менялись. Docker/HTTP effects,
полная release-приёмка и готовность основной версии не заявляются выполненными.