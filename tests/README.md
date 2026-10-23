# Тестовая инфраструктура

Все test-проекты используют xUnit v3 и VSTest/TRX. `IsTestingPlatformApplication=false` явно
сохраняет VSTest с xUnit 4.x и SDK 10; команды из quickstart не требуют перехода на MTP CLI.
Unit/VM tests выполняются без окна. Offline integration использует собственные stores и doubles,
не запускает процессы, Docker или сетевые HTTP-запросы.

`IsolatedTestStore` создаёт уникальный дочерний каталог и installation/environment IDs.
`GetPath` запрещает абсолютные пути и выход наружу. Dispose проверяет точный каталог перед
рекурсивным удалением и отказывается следовать reparse points. Production data directory запрещён.
Для независимых тестов создавайте отдельные stores, а не общий process-wide singleton.

`FaultController.RunMutationAsync` позволяет воспроизвести сбой перед эффектом и после эффекта
до получения результата. `FakeClock` изменяет наблюдаемое UTC-время явно; реальных таймеров
он не создаёт. `FakeProcessRunner` и `FakeHttpMessageHandler` выполняют только заранее заданные
ответы и функции, включая cancellation, nonzero exit и потерю HTTP-ответа после мутации.
Незапланированный вызов завершается ошибкой, без перехода к live I/O.

Live Docker cases должны иметь `[Trait("Category", "Docker")]` и создавать `ScopedDockerFixture`
через публичный factory, который читает `ENVIRONMENTBUILDER_DOCKER_TESTS=1`. Дополнительно нужен
непустой immutable manifest уникальных environment IDs, выделенных этим store. Fixture проверяет
prerequisites до effects; отсутствие opt-in или обязательной prerequisite даёт исключение, не skip.
Перед операцией проверяйте `RequireAllowedEnvironment`.

`IScopedDockerBackend` — граница к будущим production capability probes и plan/ownership cleanup.
Live adapter появится вместе с этими production services в последующих фазах. Cleanup получает
только exact manifest, не использует отменённый test token и передаёт отказ вызывающему коду.
В harness нет raw delete/prune или обхода проверки владения. Сейчас выполняются только offline
тесты этой политики с recording backend; успешная live Docker-приёмка не заявляется.

`Category=UI` запускает текущий smoke общих ресурсов. Он загружает скомпилированные словари
из App, создаёт настоящий WPF host вне видимой области, проверяет цвета, validation и DataGrid,
переключает Light → Dark → Light с сохранением текста и selection, фиксирует binding errors.
`ENVIRONMENTBUILDER_UI_ARTIFACTS` включает PNG-превью. Полная интерактивная матрица экранов,
input/focus и 100/125/150/200% DPI относится к последующим задачам.
