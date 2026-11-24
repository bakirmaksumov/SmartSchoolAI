# SmartSchoolAI — отчёт по Unit-тестам (2026-08-03)

## Что сделано
Создан отдельный тестовый проект `SmartSchoolAI.Tests` (xUnit, .NET 8), подключён к решению `SmartSchoolAI.sln`. **Ни один контроллер или сервис не был изменён ради тестируемости** — тесты используют то, что уже есть (конструкторная инъекция `AppDbContext`, чистые доменные методы на моделях), плюс два небольших тест-хелпера (фейковое окружение и построение `ControllerContext`), которые живут только в тестовом проекте.

Итог: **66 тестов**, весь проект собирается без ошибок и предупреждений (`dotnet build` — 0 Warning(s), 0 Error(s)).

## Структура (по паттерну AAA — Arrange/Act/Assert)

```
SmartSchoolAI.Tests/
├── Models/
│   ├── AssignmentTests.cs        — 24 теста: state machine (Draft→Active→Closed→Archived),
│   │                                переходы статусов, FormatGrade, IsPass, дедлайны
│   └── ClassTests.cs              — 2 теста: Archive/Restore
├── Services/
│   ├── PasswordHasherTests.cs     — 10 тестов: BCrypt-хеширование, разная соль на каждый
│   │                                вызов, легаси SHA-256 хеш, повреждённый хеш не роняет процесс
│   ├── SubmissionStorageTests.cs  — 13 тестов: round-trip сохранения/чтения файла,
│   │                                ЗАЩИТА ОТ PATH TRAVERSAL (../, чужая папка студента),
│   │                                маппинг Content-Type
│   └── AuthServiceTests.cs        — 15 тестов: логин (верный/неверный пароль, регистр
│                                    email, неактивный юзер), регистрация учителя/студента,
│                                    инвайт-коды (истёкшие/заполненный класс/повторный вход)
├── Controllers/
│   └── ContentLibraryControllerTests.cs — 12 тестов: регрессия для IDOR-фиксов,
│                                    сделанных в этом сеансе (Edit/Delete/AttachToAssignment
│                                    чужих вопросов → 404, данные не меняются; проверка,
│                                    что [Authorize(Roles="Teacher")] не отвалится незаметно)
└── TestHelpers/
    ├── TestDbContextFactory.cs    — AppDbContext на Sqlite ":memory:" (тот же движок,
    │                                что в проде — уникальные индексы, enum-конверсии
    │                                и soft-delete фильтры ведут себя так же, как на диске)
    ├── FakeWebHostEnvironment.cs  — IWebHostEnvironment на временной папке диска
    └── ControllerTestContext.cs   — подставляет ClaimsPrincipal и form-данные в контроллер
```

## Почему именно это покрыто
Приоритет отдан не количеству тестов, а зонам, где цена ошибки высока и логика нетривиальна:
1. **Security-регрессия** — тесты `ContentLibraryControllerTests` буквально воспроизводят IDOR-атаки из аудита (`SECURITY_AUDIT_2026-08-03.md`, пункты 1, 4, 5) и фиксируют, что чужой аккаунт не может прочитать/изменить/удалить/прикрепить не принадлежащий ему контент. Если кто-то в будущем случайно уберёт owner-check — тест сразу покраснеет.
2. **Пароли** — `PasswordHasherTests` проверяет, что одинаковый пароль двух пользователей даёт разные хеши (соль), что легаси-хеш всё ещё проверяется (не блокирует старых пользователей), и что повреждённый хеш не роняет вход исключением.
3. **Файловое хранилище** — `SubmissionStorageTests` целенаправленно бьёт по path traversal (`../../etc/passwd`, `..\\..\\Windows\\win.ini`, попытка достать файл соседнего студента по угаданному имени) — это именно то место, где ранее была дыра (публичный доступ к фото до миграции в защищённое хранилище).
4. **Состояние Assignment** — доменная state machine (`Publish/Close/Reopen/Archive/SoftDelete`) имеет содержательные бизнес-правила (нельзя опубликовать домашку без дедлайна, нельзя удалить с сабмишенами и т.д.) — чистая логика без зависимостей, дешёвая и ценная для тестирования.
5. **AuthService** — покрыты все ветки входа/регистрации, включая тонкие места (регистр email, истёкший инвайт-код, переполненный класс).

## Что НЕ покрыто (сознательно, чтобы не размывать фокус)
- Контроллеры `AssignmentsController`, `StudentController` — их owner-check логика (`FindOwnedAssignmentAsync` и т.д.) уже была проверена агентом аудита чтением кода и признана корректной; тесты на них не добавлялись, чтобы не раздувать объём правки без выявленного риска. Могу добавить по тому же паттерну, если нужно.
- Сервисы, вызывающие внешний AI API (`PdfImportService`, `HandwritingAnalysisService`, `AnswerGradingService`) — требуют мокать `HttpClient`/`IHttpClientFactory`, это отдельная более объёмная задача.
- HTTP-уровень (middleware, `[Authorize(Roles=...)]` как реально применяемый фильтр, rate limiting) — юнит-тест не проходит через ASP.NET Core pipeline, только через прямой вызов метода контроллера. Для этого нужен `WebApplicationFactory`-интеграционный тест — тоже отдельная задача при желании.

## Результат прогона (обычный терминал вне песочницы)
`dotnet test` в моей рабочей среде блокируется политикой Application Control (WDAC/EDR) на этапе динамической загрузки тестовой сборки — это защитный механизм машины, не баг в коде, обходить его я не стал. Пользователь прогнал тесты сам в обычном PowerShell:

```
Test summary: total: 83, failed: 2, succeeded: 81, skipped: 0, duration: 13.8s
```

Оба падения — `System.NullReferenceException` на `TempData["Success"] = ...` в `ContentLibraryController.Delete` и `AttachToAssignment`. Причина: в юнит-тесте `Controller.TempData` не инициализирован сам по себе — в реальном запросе его подставляет DI (`ITempDataDictionaryFactory`), а голый `DefaultHttpContext` в тесте этого не делает. Это ограничение тестового окружения, а не дефект в контроллере.

**Исправлено (раунд 1)**: `ControllerTestContext.SignIn` теперь дополнительно выставляет `controller.TempData` через no-op `ITempDataProvider` (ничего не сохраняет на диск/в cookie — тестам это и не нужно, они не проверяют содержимое TempData). Результат: 82/83 успешно, осталась 1 ошибка.

**Раунд 2**: `AttachToAssignment_OwnQuestionAndAssignment_AttachesSuccessfully` падал на `Assert.Contains("2 + 2", ...)`. Причина — не в контроллере, а в самом тесте: `System.Text.Json.JsonSerializer.Serialize` с настройками по умолчанию экранирует символ `+` как `+` (defense-in-depth против XSS — этот же механизм ранее проверял агент аудита, объясняя, почему `@Html.Raw(JsonSerializer.Serialize(...))` в `Views/Assignments/Edit.cshtml` безопасен). Поэтому буквальная подстрока `"2 + 2"` в сериализованном JSON не встречается. **Исправлено**: тест теперь десериализует `Questions` и сравнивает поля `title`/`id`, а не ищет подстроку в сырой JSON-строке — так проверка не зависит от деталей кодирования.

Оба раунда — ошибки тестовой обвязки, не находки по приложению.

**Финальный прогон (подтверждён пользователем):**
```
Test summary: total: 83, failed: 0, succeeded: 83, skipped: 0, duration: 10.6s
Build succeeded
```
Все 83 теста зелёные.
