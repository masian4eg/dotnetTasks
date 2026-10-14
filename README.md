# Task API (.NET 9)

ASP.NET Core Minimal API, EF Core / PostgreSQL, RabbitMQ.Client 7.

## Запуск

Нужны .NET 9 SDK и PostgreSQL. RabbitMQ необязателен для работы API: ошибки публикации логируются.

```powershell
docker compose up -d
dotnet run --project TaskApi.csproj --urls http://localhost:5080
```

Схема создаётся через `EnsureCreatedAsync` при запуске. Миграции не добавлены; это подходит для новой БД тестового задания. Для существующей схемы нужны миграции. PostgreSQL контейнера доступен на порту 5433, чтобы не конфликтовать с локальной установкой на 5432. Параметры в `appsettings.json`; переопределения через `ConnectionStrings__Tasks`, `RabbitMQ__HostName`, `RabbitMQ__UserName`, `RabbitMQ__Password` и т. д. Пароли по умолчанию предназначены для локального запуска.

```powershell
$task = Invoke-RestMethod http://localhost:5080/tasks -Method Post -ContentType 'application/json' -Body '{"title":"Buy milk","priority":"High"}'
Invoke-RestMethod "http://localhost:5080/tasks/$($task.id)/complete" -Method Put
Invoke-RestMethod http://localhost:5080/tasks
Invoke-RestMethod "http://localhost:5080/tasks/$($task.id)" -Method Delete
```

POST возвращает 201, GET и успешное завершение — 200, DELETE — 204. Некорректные title/enum/guid — 400, отсутствующая задача — 404, повторное или конкурентное завершение — 409. Priority передаётся строкой: Low, Medium, High; по умолчанию Medium. Все даты UTC с микросекундной точностью PostgreSQL. CompletedAt отсутствует до завершения; повторное завершение не меняет дату.

## Как устроено

- `Program.cs`: регистрация зависимостей, инициализация БД и запуск.
- `Endpoints/TaskEndpoints.cs`: HTTP-обработчики группы `/tasks`.
- `Models/TaskItem.cs`: сущность, DTO, событие.
- `DbManager/TaskDbContext.cs`: `uint RowVersion` + `IsRowVersion()` отображается Npgsql на PostgreSQL `xmin`. EF проверяет версию при UPDATE/DELETE; проигравший конкурентный запрос получает 409. Это не SQL Server byte[] rowversion.
- `Messaging/TaskEventPublisher.cs`: очередь на 1000 событий и один фоновый читатель. API ставит событие в очередь строго после успешного SaveChanges. Нет ожидания брокера в HTTP-запросе, .Wait() или .Result.
- `Messaging/RabbitMqTransport.cs`: ленивое подключение, durable topic exchange `task.events`, routing key `task.completed`, JSON со строковым priority и persistent delivery mode. Confirms отключены. Канал используется последовательно.

При недоступности RabbitMQ попытка ограничена двумя секундами; событие целиком пишется в warning-лог и не повторяется. Следующая публикация при закрытом канале заново подключается. Очередь ограничена, при переполнении событие также пишется в лог.

При shutdown очередь закрывается для записи и получает до 4 секунд на отправку; оставшиеся события логируются. Ещё до 1 секунды выделено на закрытие и освобождение канала/соединения. Host shutdown timeout — 5 секунд.

Это best effort, без outbox: аварийное завершение процесса между коммитом и публикацией может потерять событие. Без confirms нет гарантии приёма брокером. Для получения сообщений надо заранее привязать очередь к exchange с ключом `task.completed` (например, в RabbitMQ UI http://localhost:15672, guest/guest); exchange сам сообщения не хранит. Consumer в приложении не нужен.

## Тесты

```powershell
dotnet test Tests/TaskApi.Tests.csproj
```

WebApplicationFactory запускает настоящий HTTP-конвейер API. В `Tests/CompleteTaskIntegrationTest.cs` каждый тест проверяет отдельное правило: создание, завершение, повторное завершение, удаление, список или валидацию. БД заменяется на EF InMemory, а издатель событий — на синхронную записывающую заглушку. Поэтому проверка отсутствия повторного события не зависит от скорости фонового обработчика. Заглушка читает задачу через отдельный DbContext при Enqueue: это проверяет порядок сохранения и постановки события в очередь, но InMemory не доказывает настоящий коммит PostgreSQL.

Отдельно проверяются отказ SaveChanges (409 без события) и сбой транспорта с настоящим фоновым издателем (завершение остаётся сохранённым). `Tests/TaskEventPublisherTest.cs` проверяет продолжение обработки после ошибки публикации и отправку накопленных событий по порядку перед закрытием транспорта.

InMemory не моделирует PostgreSQL xmin. Инфраструктурные сценарии разделены:

- `Tests/PostgreSqlCompletionIntegrationTest.cs`: настоящий PostgreSQL и записывающий издатель. EF interceptor синхронизирует два запроса, читающих одну версию до UPDATE. Проверяется один 200, один 409, сохранённое состояние и единственное событие, поставленное в очередь после сохранения. Тест удаляет только свою строку.
- `Tests/RabbitMqTransportIntegrationTest.cs`: настоящий RabbitMQ и вызов нашего RabbitMqTransport. Проверяется маршрут, JSON и свойства доставки события через временную exclusive очередь. HTTP API и PostgreSQL для этого сценария не нужны.

```powershell
docker compose up -d --wait
$env:RUN_INFRASTRUCTURE_TESTS = '1'
dotnet test Tests/TaskApi.Tests.csproj
```

После рефакторинга проверено: 19 обычных тестов и 2 инфраструктурных теста прошли с локальными PostgreSQL и RabbitMQ.

Без переменной окружения два инфраструктурных теста пропускаются. Для обычного запуска нужен установленный SDK; здесь также сохранён локальный SDK в `.sdk` (исключён из Git): вместо `dotnet` можно использовать `./.sdk/dotnet.exe`.

На следующем шаге добавил бы Testcontainers для изоляции инфраструктурных тестов и миграции; для гарантированной доставки — outbox и confirms.



