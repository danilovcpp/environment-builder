# Тестовая инфраструктура

Все test-проекты используют xUnit v3 и VSTest/TRX. `IsTestingPlatformApplication=false` явно
сохраняет VSTest с xUnit 4.x и SDK 10; команды из quickstart не требуют перехода на MTP CLI.
Unit/VM tests выполняются без окна. Integration использует собственные stores и doubles,
не запускает Docker или сетевые HTTP-запросы. Тест process adapter запускает только
локальный дочерний процесс, генерирующий контролируемые stdout/stderr.

`IsolatedTestStore` создаёт уникальный дочерний каталог и installation/environment IDs.
`GetPath` запрещает абсолютные пути и выход наружу. Dispose проверяет точный каталог перед
рекурсивным удалением и отказывается следовать reparse points. Production data directory запрещён.
Для независимых тестов создавайте отдельные stores, а не общий process-wide singleton.

`FaultController.RunMutationAsync` позволяет воспроизвести сбой перед эффектом и после эффекта
до получения результата. `FakeClock` изменяет наблюдаемое UTC-время явно; реальных таймеров
он не создаёт. `FakeProcessRunner` и `FakeHttpMessageHandler` выполняют только заранее заданные
ответы и функции, включая cancellation, nonzero exit и потерю HTTP-ответа после мутации.
Незапланированный вызов завершается ошибкой. Проверки runtime, владения ресурсами,
ошибок и восстановления используют подготовленные Docker/HTTP responses и fault injection.
Вся integration suite запускается без дополнительных переменных окружения и фильтров.

`Category=UI` запускает smoke общих ресурсов, authoring и Inspect. Проверка ресурсов загружает скомпилированные словари
из App, создаёт настоящий WPF host вне видимой области, проверяет цвета, validation и DataGrid,
переключает Light → Dark → Light с сохранением текста и selection, фиксирует binding errors.
`ENVIRONMENTBUILDER_UI_ARTIFACTS` включает PNG-превью. Полная интерактивная матрица экранов,
input/focus и 100/125/150/200% DPI относится к последующим задачам.
