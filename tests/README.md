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
Обычная integration suite запускается без дополнительных переменных окружения.
Отдельный opt-in тест `Category=DockerLive` пропускается по умолчанию. Он проверяет
настоящую цепочку Settings → создание → Start: инфраструктура, целевой image,
локальная сборка, HTTP и env override. Нужны локальные образы `redis:7.2-alpine`
и `nginx:stable-alpine`. Тест создаёт собственное окружение и удаляет его через
production-проверку владения; при неудаче очистки сохраняет отдельный store.

```powershell
$env:ENVIRONMENTBUILDER_LIVE_DOCKER = '1'
dotnet test tests/EnvironmentBuilder.IntegrationTests/EnvironmentBuilder.IntegrationTests.csproj --configuration Release --no-build --no-restore --filter 'Category=DockerLive'
Remove-Item Env:ENVIRONMENTBUILDER_LIVE_DOCKER
```

`Category=UI` запускает smoke общих ресурсов, authoring и Inspect. Проверка ресурсов загружает скомпилированные словари
из App, создаёт настоящий WPF host вне видимой области, проверяет цвета, validation и DataGrid,
переключает Light → Dark → Light с сохранением текста и selection, фиксирует binding errors.
`ENVIRONMENTBUILDER_UI_ARTIFACTS` включает PNG-превью. Проверяются обе темы и окна 960×600,
1280×720 и 1672×941 DIP, все вкладки настроек компонента, длинный однострочный и многострочный
текст, focus/selection/validation без сдвигов, редакторы таблиц, скругления и ширина scrollbar.
Отдельная матрица реальной мышью проверяет hover/pressed/disabled кнопок и элементов выбора.
Тесты используют текущий DPI рабочего стола; полный проход на 100/125/150/200% требует
запуска при соответствующем системном масштабе.
