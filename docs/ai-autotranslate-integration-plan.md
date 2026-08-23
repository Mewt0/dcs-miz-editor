# MizEdit: план интеграции автоперевода через AI-агентов

Дата: 2026-08-23

## Короткий вывод

Автоперевод лучше делать не как замену текущей кнопки Ollama, а как отдельный batch pipeline:

```text
открытая .miz / папка .miz
  -> export manifest + chunks
  -> AI agent provider / MCP server
  -> strict validate
  -> preview
  -> apply to translation workspace
  -> backup + save .miz
```

Главное правило: AI может готовить перевод, но не должен сам напрямую менять `.miz` без preview и валидации. Это сохранит миссии от сломанного Lua, DictKey, маркеров, частот, координат и ресурсов.

## Что уже есть в проекте

В MizEdit уже есть почти вся база для безопасного автоперевода:

- `Services/ITranslationProvider.cs` - простой интерфейс построчного провайдера.
- `Services/TranslationQueueRunner.cs` - очередь AI-перевода с прогрессом, отменой и ошибками.
- `Services/OllamaTranslationService.cs` - локальный построчный перевод, защита токенов и Lua-like текста.
- `Services/GeminiBatchTranslationService.cs` - batch-перевод через Gemini, JSON response, retries, защита маркеров.
- `Core/TranslationBatchDocument.cs` - manifest, compact `Batch: ...` + `1»` формат, анализ импортированного ответа.
- `Views/TranslationWorkspace.xaml` - готовая рабочая область переводов, copy/paste batch, preview-friendly UX.
- `Views/TranslationImportPreviewWindow.xaml` - окно проверки перед применением.
- `scripts/Translate-F4ETraining.ps1` - внешний прототип export/import/gemini для пачки обучающих миссий.
- `docs/ai-agent-translation-automation.md` - первая версия идеи export -> agents -> validate -> preview -> apply.

Вывод: интеграция должна переиспользовать `TranslationBatchDocument`, preview и очередь, а не делать новый параллельный редактор.

## Что сказали агенты

Я спросил два независимых worker-агента. Их рекомендации совпали по главным пунктам:

- Вынести AI-агентов за отдельный интерфейс вроде `IAiAgentClient` / `IAgentTranslationService`.
- Хранить API keys не в обычном json, а через Windows DPAPI или Credential Manager.
- Делать UI асинхронным: progress, cancel, retry, понятные ошибки.
- MCP server должен иметь health check, timeout, version check и fallback.
- Ответ агента всегда валидировать по схеме: число строк, ключи, маркеры, protected tokens, отсутствие Markdown.
- Применение в `.miz` только после preview и подтверждения пользователя.

Мой вывод: берем эти идеи, но адаптируем под текущий MizEdit. MVP должен быть проще: сначала один `Agent Batch Translation` режим в существующем Translation Workspace, потом уже красивые профили, фоновая очередь и перевод папок.

## Предлагаемая архитектура

Добавить новый слой `AI Agent Translation`, который сидит между UI и существующим batch-протоколом.

```text
WPF UI
  MainWindow / TranslationWorkspace
  AiTranslationSettingsWindow
  AiTranslationRunWindow or panel

Application services
  AiTranslationPipeline
  AgentRunStore
  TranslationBatchValidator
  TranslationPreviewAdapter

Provider layer
  IAiAgentClient
  McpAgentClient
  GeminiAgentClient
  OllamaAgentClient or existing adapter

Infrastructure
  McpServerHost
  SecureSecretStore
  AiTranslationSettingsStore
  Publish/bundled tools folder
```

## Новый UI

### Главное меню

Добавить отдельное меню или кнопку в верхней панели:

```text
AI перевод
  Включить AI-агентов
  Перевести видимые строки
  Перевести выбранные строки
  Перевести все пропущенные
  Перевести папку миссий...
  Импортировать результат агентов...
  Настройки AI...
```

В текущем `TranslationWorkspace` можно заменить текст "Ollama" на нейтральное "AI provider", потому что дальше там будут Ollama, Gemini, MCP-agent и, возможно, другие провайдеры.

### Окно настроек AI

Сделать отдельное окно `AiTranslationSettingsWindow`.

Разделы:

- `Общее`
  - `Включить AI-агентов в MizEdit`
  - `Провайдер по умолчанию`: MCP agent / Gemini / Ollama
  - `Язык перевода`: Russian по умолчанию
  - `Останавливаться на preview`: включено и лучше не выключать в первой версии

- `MCP сервер`
  - `Режим`: bundled / external exe / external URL
  - `Путь к серверу`: если пользователь хочет указать расположение сам
  - `URL`: например `http://127.0.0.1:8765`
  - `Кнопка Проверить`
  - `Автозапуск вместе с MizEdit`
  - `Остановить сервер при закрытии MizEdit`

- `API ключи`
  - Gemini API key
  - OpenAI-compatible API key, если понадобится
  - кнопки `Показать`, `Проверить`, `Удалить`
  - ключи должны быть masked и не попадать в логи

- `Пакеты и качество`
  - `Chunk size`: 20-30 для обычных агентов, 60-80 для сильных моделей
  - `Deduplicate repeated source lines`: включено
  - `Preserve technical tokens`: включено всегда
  - `Retry failed chunks`: 2-3 попытки
  - `Temperature`: 0

- `Папки`
  - `Work root`: где хранить run-папки, manifest, chunks, reports
  - `Backup root`: где хранить копии перед заменой `.miz`

## Настройки и ключи

Обычные настройки можно хранить в `%AppData%\MizEdit\ai-translation-settings.json`.

Пример того, что можно хранить открыто:

```json
{
  "enabled": true,
  "defaultProvider": "mcp",
  "mcpMode": "bundled",
  "mcpServerPath": "",
  "mcpServerUrl": "http://127.0.0.1:8765",
  "autoStartMcp": true,
  "stopMcpOnExit": true,
  "chunkSize": 30,
  "deduplicate": true,
  "stopAtPreview": true
}
```

Что нельзя хранить открыто:

- API keys
- bearer tokens
- refresh tokens
- любые ключи внешних LLM-провайдеров

Для секретов:

- MVP: Windows DPAPI через `ProtectedData`.
- Лучше позже: Windows Credential Manager через маленький wrapper.
- В `translation_tool_settings.json` или app settings писать только имя секрета, но не сам ключ.

## MCP server: как "прилепить к билду"

Нужны 3 режима, чтобы не загнать себя в угол:

### 1. Bundled server

MizEdit поставляется с папкой:

```text
MizEdit.exe
tools/
  mizedit-agent-server/
    mizedit-agent-server.exe
    config.example.json
```

MizEdit при запуске проверяет наличие `tools/mizedit-agent-server/mizedit-agent-server.exe`.

Если пользователь включил `Автозапуск MCP`, приложение:

- запускает сервер как child process;
- передает ему порт и путь к временной run-папке;
- делает health check;
- показывает статус `MCP server ready`;
- при закрытии MizEdit завершает процесс.

### 2. External exe

Пользователь указывает путь к своему MCP server:

```text
C:\Tools\my-mcp-server\server.exe
```

MizEdit запускает его так же, но не обновляет и не перезаписывает.

### 3. External URL

Пользователь указывает URL уже запущенного сервера:

```text
http://127.0.0.1:8765
```

MizEdit ничего не запускает, только делает health check и отправляет tasks.

## Минимальный контракт MCP/агента

Не стоит отдавать агенту `.miz` целиком. Лучше отдавать только безопасный translation package.

Request:

```json
{
  "protocol": "mizedit.translation.v1",
  "runId": "20260823-143000-f4e-01",
  "targetLanguage": "ru",
  "rules": {
    "preserveDcsTokens": true,
    "preserveLineBreaks": true,
    "noMarkdown": true
  },
  "items": [
    {
      "n": 1,
      "k": "DictKey_ActionText_105",
      "source": "CONTACT NELLIS DEPARTURE"
    }
  ]
}
```

Response:

```json
{
  "protocol": "mizedit.translation.v1",
  "runId": "20260823-143000-f4e-01",
  "items": [
    {
      "n": 1,
      "k": "DictKey_ActionText_105",
      "translation": "СВЯЖИТЕСЬ С NELLIS DEPARTURE"
    }
  ],
  "warnings": []
}
```

Для очень больших миссий можно оставить JSONL chunks:

```jsonl
{"n":1,"k":"DictKey_ActionText_105","t":"Свяжитесь с NELLIS DEPARTURE"}
{"n":2,"k":"DictKey_ActionText_106","t":"Дозаправка завершена, свяжитесь с DARKSTAR"}
```

Важно: агент не обязан возвращать `source`. MizEdit уже знает source из manifest. Это экономит контекст.

## Pipeline подробно

### 1. Select scope

Пользователь выбирает:

- выбранные строки;
- все видимые строки;
- только пустые;
- текущая миссия;
- папка миссий.

Для первой версии достаточно: выбранные, видимые, пропущенные.

### 2. Export

MizEdit строит `TranslationBatchManifest` через существующий `TranslationBatchDocument`.

Run folder:

```text
%AppData%\MizEdit\agent-runs\run-20260823-143000\
  manifest.json
  chunks\
    chunk-001.jsonl
    chunk-002.jsonl
  translated\
  report.md
  log.txt
```

### 3. Agent execution

`AiTranslationPipeline` отправляет chunks в выбранный provider.

Provider options:

- `McpAgentClient`: общается с MCP server.
- `GeminiAgentClient`: можно адаптировать текущий `GeminiBatchTranslationService`.
- `OllamaAgentClient`: можно оставить для local/private режима.

### 4. Validate

Проверки до preview:

- JSON/JSONL парсится.
- Все `n` существуют в manifest.
- Все `k` совпадают.
- Нет лишних строк.
- Нет пропущенных обязательных строк.
- `translation` не пустой для строк, которые агент должен был перевести.
- Нет Markdown fences.
- Protected fragments сохранены.
- Lua-like строки не были переведены, если они были исключены.
- Частоты, координаты, пути, имена файлов, `%s`, `{0}`, `\n`, `DictKey`, `ResKey`, `CLSID` не повреждены.
- Подозрительная длина помечается warning, а не всегда fatal.

### 5. Preview

Использовать существующее `TranslationImportPreviewWindow`, но расширить его для agent runs:

- показать translated;
- unchanged;
- failed;
- suspicious;
- missing;
- rejected;
- кнопки `Apply accepted`, `Retry failed`, `Export rejected chunk`.

### 6. Apply

Только после подтверждения пользователя:

- применить accepted translations к `TranslationEntry`;
- отметить dirty state;
- сохранить checkpoint;
- обновить stats;
- дать пользователю вручную просмотреть таблицу.

### 7. Save .miz

При сохранении:

- создать backup `.miz`;
- записать locale;
- validate reopen;
- показать report.

Для folder mode:

- never overwrite originals без явного чекбокса;
- по умолчанию писать в `translated-miz`;
- `Replace originals` только после отдельного подтверждения.

## Предлагаемые новые классы

### `AiTranslationSettings`

Модель настроек без секретов:

- `Enabled`
- `DefaultProvider`
- `McpMode`
- `McpServerPath`
- `McpServerUrl`
- `AutoStartMcp`
- `StopMcpOnExit`
- `ChunkSize`
- `Deduplicate`
- `StopAtPreview`
- `WorkRoot`

### `IAiSecretStore`

Интерфейс для ключей:

```csharp
public interface IAiSecretStore
{
    Task SaveSecretAsync(string name, string value, CancellationToken cancellationToken);
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken);
    Task DeleteSecretAsync(string name, CancellationToken cancellationToken);
}
```

### `IAiAgentClient`

Batch-level клиент:

```csharp
public interface IAiAgentClient
{
    Task<AiAgentHealth> CheckHealthAsync(CancellationToken cancellationToken);
    Task<AiTranslationChunkResult> TranslateChunkAsync(
        AiTranslationChunkRequest request,
        CancellationToken cancellationToken);
}
```

### `IMcpServerHost`

Управление bundled/external процессом:

- `StartAsync`
- `StopAsync`
- `CheckHealthAsync`
- `IsOwnedProcess`
- `ServerUrl`

### `AiTranslationPipeline`

Оркестратор:

- принимает scope;
- строит manifest;
- режет chunks;
- вызывает provider;
- валидирует;
- собирает import plan;
- открывает preview через UI callback.

### `AgentRunStore`

Файловое хранение run-папок:

- manifest;
- chunks;
- translated chunks;
- report;
- resumable state.

## Как связать с текущим кодом

### Короткий MVP

1. Добавить `AiTranslationSettingsWindow`.
2. Добавить `SecureSecretStore`.
3. Добавить `McpAgentClient` или сначала `GeminiAgentClient`.
4. Добавить `AiTranslationPipeline`, который использует `TranslationBatchDocument`.
5. В `TranslationWorkspace` добавить кнопку `Перевести видимые через агента`.
6. Ответ прогонять через текущий import/preview механизм.
7. После preview применять к `TranslationEntry`, не сразу к `.miz`.

### Что не стоит делать в MVP

- Не давать агенту прямой доступ к `.miz`.
- Не позволять auto-apply без preview.
- Не хранить API key в `translation_tool_settings.json`.
- Не смешивать MCP lifecycle прямо в `MainWindow.xaml.cs`; лучше маленький service.
- Не делать сначала перевод папки, пока не стабилен перевод одной миссии.

## UX состояния

Нужно показывать человеку не только progress bar, но и понятные статусы:

```text
AI agents disabled
MCP server not configured
Starting bundled MCP server...
MCP server ready
Exporting 312 rows...
Translating chunk 4/18...
Validating response...
Preview ready: 298 accepted, 9 warnings, 5 rejected
Applied to workspace, save mission when ready
```

Ошибки:

- `MCP server not found`: показать путь и кнопку выбрать файл.
- `MCP health check failed`: показать URL, порт, кнопку retry.
- `API key missing`: открыть settings на вкладке keys.
- `Quota exceeded`: предложить уменьшить batch size или сменить provider.
- `Chunk invalid`: сохранить rejected chunk и показать причину.
- `Mission changed after export`: запретить import до обновления manifest.

## Безопасность

Обязательные правила:

- API keys не логировать.
- API keys не класть в report.
- При exception скрывать headers и query params.
- Если MCP server запускается как child process, не передавать ключи через command line. Лучше environment variables или локальный защищенный config.
- Для cloud provider явно писать в UI, что текст миссии будет отправлен внешнему сервису.
- Для local MCP/Ollama писать, что данные остаются локально, если сервер локальный.

Нужна маленькая пометка в settings:

```text
Cloud providers receive mission text for translation. Do not use cloud mode for private missions unless you accept that.
```

## Почему batch-agent лучше построчного TranslateAsync

Текущий `ITranslationProvider.TranslateAsync(string source)` хорош для Ollama-кнопки, но для агентов он узковат:

- агентам нужен контекст нескольких строк;
- нужно сохранять chunks и run state;
- нужен retry по chunk, а не только по строке;
- нужен preview report;
- нужно валидировать пакет целиком;
- перевод папки миссий невозможен удобно через один `string -> string`.

Поэтому оставляем `ITranslationProvider` для простого режима, но добавляем новый batch interface. Старый `TranslationQueueRunner` можно потом адаптировать поверх нового pipeline, но не обязательно ломать его в первой версии.

## Автоматизация через AI-агентов

Есть два уровня автоматизации.

### Уровень 1: встроенный provider

MizEdit сам вызывает модель или MCP server:

```text
MizEdit -> McpAgentClient -> MCP server -> LLM provider -> response -> MizEdit validator
```

Это лучший вариант для обычного пользователя.

### Уровень 2: agent run folder

MizEdit создает папку задач, а внешние агенты заполняют `translated`.

```text
MizEdit export -> run folder -> external agents -> MizEdit import/validate/preview
```

Это лучший вариант для нас при разработке, больших миссий и отладки качества. Текущий `Translate-F4ETraining.ps1` уже похож на этот режим.

В UI это можно назвать:

- `Автоматически через настроенного агента`
- `Экспортировать задачу для внешних агентов`
- `Импортировать результат внешних агентов`

## Roadmap

### Phase 1: Settings and secure storage

- Добавить окно настроек AI.
- Добавить хранение API keys через DPAPI.
- Добавить health check для provider.
- В UI заменить жесткое "Ollama" на "AI".

### Phase 2: Single mission agent pipeline

- Добавить `AiTranslationPipeline`.
- Добавить compact JSONL chunk export.
- Добавить provider для Gemini или MCP.
- Использовать существующий preview перед применением.
- Добавить retry failed chunks.

### Phase 3: Bundled MCP server

- Положить сервер в `tools/mizedit-agent-server`.
- Добавить `IMcpServerHost`.
- Добавить режимы bundled/external path/external URL.
- Добавить version/health endpoint.
- Добавить publish rule, чтобы сервер попадал в build zip.

### Phase 4: Folder translation

- UI выбора папки `.miz`.
- Run report на каждую миссию.
- Output folder `translated-miz`.
- Backup originals только при явном replace.
- Validate reopen после каждой миссии.

### Phase 5: Quality tools

- Glossary для DCS/авиации.
- Blacklist/keep-list терминов.
- Translation memory для повторяющихся строк.
- Compare original vs translated.
- Автоматические тесты на protected token preservation.

## Риски

### MCP lifecycle

Если сервер запускается рядом с билдом, можно оставить висящие процессы после закрытия MizEdit. Нужен owned process tracking и stop on exit.

### API compatibility

MCP server может поменять контракт. Нужен `protocolVersion` и health response:

```json
{
  "name": "mizedit-agent-server",
  "protocol": "mizedit.translation.v1",
  "version": "0.1.0"
}
```

### Сломанные миссии

AI может изменить Lua, ключи, координаты или плейсхолдеры. Нужны strict validators и preview.

### Стоимость и лимиты

Большие обучающие миссии быстро едят лимиты. Нужны chunk size, deduplication, retry и resume.

### UX доверия

Пользователь должен видеть, что именно будет применено. Поэтому preview обязателен, а auto-save лучше добавить только позже.

## Мой совет по реализации

Я бы делал так:

1. Сначала привести текущий AI UI к нейтральному виду: не `Ollama`, а `AI provider`.
2. Добавить `AI settings` с включением агентов, MCP path/URL и API keys.
3. Реализовать `SecureSecretStore` через DPAPI.
4. Реализовать `AiTranslationPipeline` для одной открытой миссии.
5. Первым provider сделать Gemini adapter, потому что `GeminiBatchTranslationService` уже есть и проще проверить end-to-end.
6. Потом добавить `McpAgentClient`.
7. Потом добавить bundled MCP server в build zip.
8. Только после стабильного preview/apply добавить folder translation.

Так мы не ломаем текущий рабочий перевод, но постепенно превращаем его в нормальную агентную систему.

## Минимальная первая задача для кодинга

Сформулировать как issue:

```text
Добавить настройки AI-перевода:
- окно AI Settings;
- toggle Enable AI agents;
- provider selector: Ollama / Gemini / MCP;
- MCP path + MCP URL;
- secure API key storage through DPAPI;
- test connection button;
- no direct changes to .miz.
```

Вторая задача:

```text
Добавить AiTranslationPipeline для текущей миссии:
- build TranslationBatchManifest from visible/selected entries;
- export compact chunks;
- call selected batch provider;
- validate response;
- open TranslationImportPreviewWindow;
- apply accepted rows to TranslationEntry only after confirmation.
```

Третья задача:

```text
Добавить bundled MCP server mode:
- include tools/mizedit-agent-server in publish output;
- start/stop server from MizEdit;
- health/version check;
- external path and external URL fallback.
```

## Дополнение после обсуждения с агентами

### Карта текущего кода MizEdit для другой нейронки

Этот раздел нужен, чтобы следующая модель/агент не проектировала функцию “с нуля”. В проекте уже есть большая часть логики batch-перевода, preview и защиты. Новую автоматизацию нужно встраивать поверх этих классов.

#### Главные файлы

```text
Core/
  TranslationEntry.cs
    Одна строка/ключ перевода в UI. Хранит Key, SourceText, Translation,
    dirty/undo/work status.

  TranslationBatchProtocol.cs
    DTO и контракты batch-протокола:
    TranslationBatchItemId
    TranslationBatchManifest
    TranslationBatchManifestItem
    TranslationBatchImportPlan
    TranslationBatchImportItem
    TranslationImportProblem
    TranslationCorpusMetrics

  TranslationBatchDocument.cs
    Главная batch-логика:
    Build(...)
    BuildManifest(...)
    FormatSource(...)
    Analyze(...)
    Apply(...)
    parsing MZ1/M2/legacy/CompactNumbered
    deduplication
    stale-plan guard
    preview statuses

  TranslationWorkspaceViewModel.cs
    Background snapshot/filter/metrics для translation workspace.

Services/
  ITranslationProvider.cs
    Старый простой интерфейс построчного перевода string -> string.

  TranslationQueueRunner.cs
    Построчная очередь AI-перевода с progress/cancel/errors.

  GeminiBatchTranslationService.cs
    Уже есть batch-провайдер с JSON, retry, token protection.

  OllamaTranslationService.cs
    Локальный простой provider + Lua/technical guards.

  TranslationCheckpointService.cs
    Checkpoint для восстановления прогресса перевода.

Views/
  TranslationWorkspace.xaml(.cs)
    UI рабочей области перевода: copy/paste batch, filters, AI buttons.

  TranslationImportPreviewWindow.xaml(.cs)
    Preview перед применением batch-вставки.

MainWindow.xaml(.cs)
  Сейчас слишком большой orchestration layer.
  Здесь подключены события TranslationWorkspace и текущая AI queue.

scripts/
  Translate-F4ETraining.ps1
    Прототип внешнего pipeline export/import/gemini/folder missions.

tests/
  GreenLine.Integration/Program.cs
    Главный regression harness: batch protocol, protected tokens,
    queue, workspace, save analysis, .miz smoke validation.

  F4E.Translation/Program.cs
    CLI/prototype для agent-export и agent-import JSONL.
```

#### Уже существующий batch-контракт

Не надо создавать второй независимый формат для preview/apply. Есть текущий контракт:

```csharp
public readonly record struct TranslationBatchItemId(string DictKey, int PartIndex);

public sealed record TranslationBatchManifestItem(
    TranslationBatchItemId Id,
    int LegacyNumber,
    TranslationEntry Entry,
    string SourceLine,
    string TranslationLine,
    TranslationBatchItemId RepresentativeId,
    int AliasCount,
    int? ExportNumber = null);

public sealed class TranslationBatchManifest
{
    public IReadOnlyList<TranslationBatchManifestItem> Items { get; }
    public IReadOnlyList<TranslationBatchManifestItem> ExportItems { get; }
    public bool Deduplicated { get; }
    public string Fingerprint { get; }
    public string ContentFingerprint { get; }
    public string ShapeFingerprint { get; }
    public string BatchId { get; }
    public string? GeneratedPrompt { get; }
    public int AliasCount { get; }
}
```

Важно:

- `Items` = все физические строки;
- `ExportItems` = только представители после dedup;
- `ExportNumber` = короткий номер `1»`;
- `BatchId` = короткий id партии;
- `Fingerprint/ShapeFingerprint` = stale guard, чтобы не применить старый preview к изменившейся миссии.

#### Уже существующие статусы preview/import

```csharp
public enum TranslationBatchImportStatus
{
    Change,
    Unchanged,
    Missing,
    SkippedExisting,
    Rejected,
    SuspiciousLength
}

[Flags]
public enum TranslationBatchImportFlags
{
    None = 0,
    PossiblyUntranslated = 1,
    SuspiciousLength = 2,
    MissingBatchId = 4,
    WrongBatchId = 8
}

public enum TranslationImportProblemKind
{
    EmptyInput,
    EmptyManifest,
    InvalidMarker,
    UnknownMarker,
    InvalidPartIndex,
    DuplicateMarkerConflict,
    KeyMismatch,
    AmbiguousPlainText,
    StalePlan,
    MissingBatchId,
    WrongBatchId,
    PasteTooLarge,
    SuspiciousLength,
    PossiblyUntranslated
}
```

Совет: для agent-run лучше расширять эти статусы/проблемы, а не делать новый параллельный enum.

Возможные новые problem kinds:

```csharp
WrongRunId,
WrongProtocolVersion,
MissingChunk,
InvalidChunkJson,
ProviderRefusal,
ProtectedTokenMismatch,
RateLimited,
CancelledByUser
```

#### Уже существующий Analyze/Apply

Ключевая логика уже разделена правильно:

```csharp
public static TranslationBatchImportPlan Analyze(
    string text,
    TranslationBatchManifest manifest,
    bool overwriteExisting,
    string? targetLocale = null);

public static TranslationBatchImportResult Apply(
    TranslationBatchImportPlan plan);
```

Правило для новой автоматизации:

```text
AI-agent response -> normalize to existing batch text/plan -> Analyze -> preview -> Apply
```

Не надо делать так:

```text
AI-agent response -> directly entry.Translation = ...
```

Иначе мы потеряем stale guard, preview statuses, rejected blocks, dedup fan-out и защиту от старого manifest.

#### Текущий copy/paste flow в MainWindow

Сейчас copy batch примерно такой:

```csharp
private async void CopyBatch_Click(object sender, RoutedEventArgs e)
{
    if (!await RefreshBatchWorkspaceAsync())
        return;

    var manifest = TranslationBatchDocument.BuildManifest(
        _translationBatchLines,
        TranslationWorkspaceView.DeduplicateOption.IsChecked == true);

    var exportFormat = TranslationBatchExportFormat.CompactNumbered;

    var text = TranslationBatchDocument.FormatSource(
        manifest,
        TranslationWorkspaceView.IncludeInstructionOption.IsChecked == true,
        exportFormat);

    Clipboard.SetText(text);
    _lastCopiedTranslationManifest = manifest;
}
```

Сейчас наружу должен уходить короткий формат:

```text
Batch: f3elNfml
1» CONTACT NELLIS DEPARTURE
2» REFUEL COMPLETE, CONTACT DARKSTAR
```

Старые `M2/MZ1/[[N]]` форматы должны остаться только для входной совместимости.

Paste batch сейчас примерно такой:

```csharp
private void PasteBatch_Click(object sender, RoutedEventArgs e)
{
    var text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
    var manifest = SelectClipboardManifest(text ?? string.Empty);

    var plan = TranslationBatchDocument.Analyze(
        text ?? string.Empty,
        manifest,
        TranslationWorkspaceView.PasteOverwriteOption.IsChecked == true,
        _translationLocale ?? CurrentLocale);

    if (!TranslationWorkspaceView.ShowImportPreview(plan))
        return;

    TranslationBatchImportResult result = new(0, 0, 0);
    RunTranslationBulkUpdate(() => result = TranslationBatchDocument.Apply(plan));
}
```

Новый agent pipeline должен вести себя похоже, только вместо `Clipboard.GetText()` источник будет `translated/chunk-*.jsonl` или MCP response.

#### Текущий построчный AI queue

Сейчас есть простой provider:

```csharp
public interface ITranslationProvider
{
    Task<string> TranslateAsync(
        string source,
        CancellationToken cancellationToken = default);
}
```

И runner:

```csharp
public sealed class TranslationQueueRunner
{
    public TranslationQueueState State { get; } = new();

    public async Task RunAsync(
        IReadOnlyList<TranslationEntry> targets,
        bool overwriteExisting,
        bool clearErrors = true,
        CancellationToken cancellationToken = default);

    public void Cancel();
}
```

Это полезно оставить для маленьких задач, но для agent/MCP batch-перевода интерфейс слишком узкий, потому что:

- нет manifest;
- нет chunks;
- нет run folder;
- нет batch preview;
- нет retry per chunk;
- нет provider cost/token report;
- нет folder missions mode.

Поэтому новый интерфейс должен быть batch-level, а старый `ITranslationProvider` можно потом адаптировать как fallback.

#### Предлагаемые новые интерфейсы с учётом текущего кода

```csharp
public sealed record AiTranslationSettings(
    bool Enabled,
    string DefaultProvider,
    string McpMode,
    string? McpServerPath,
    string? McpServerUrl,
    bool AutoStartMcp,
    bool StopMcpOnExit,
    int ChunkSize,
    int MaxParallelChunks,
    int RetryCount,
    bool Deduplicate,
    bool StopAtPreview,
    string TargetLanguage,
    string WorkRoot);
```

```csharp
public interface IAiSecretStore
{
    Task SaveSecretAsync(string name, string value, CancellationToken cancellationToken = default);
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default);
}
```

```csharp
public interface IAiAgentClient
{
    Task<AiAgentHealth> CheckHealthAsync(CancellationToken cancellationToken = default);

    Task<AiTranslationChunkResult> TranslateChunkAsync(
        AiTranslationChunkRequest request,
        CancellationToken cancellationToken = default);
}
```

```csharp
public interface IMcpServerHost
{
    Uri? ServerUri { get; }
    bool IsRunning { get; }
    bool IsOwnedProcess { get; }

    Task StartAsync(AiTranslationSettings settings, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<AiAgentHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}
```

```csharp
public sealed class AiTranslationPipeline
{
    public Task<AiTranslationRun> ExportAsync(
        AiTranslationScope scope,
        AiTranslationSettings settings,
        CancellationToken cancellationToken);

    public Task<AiTranslationRun> TranslateAsync(
        AiTranslationRun run,
        IAiAgentClient client,
        CancellationToken cancellationToken);

    public Task<TranslationBatchImportPlan> BuildPreviewPlanAsync(
        AiTranslationRun run,
        bool overwriteExisting,
        string targetLocale,
        CancellationToken cancellationToken);

    public Task<TranslationBatchImportResult> ApplyAsync(
        TranslationBatchImportPlan plan,
        CancellationToken cancellationToken);
}
```

#### Как agent chunks можно связать с текущим Analyze

Если MCP возвращает compact JSONL:

```jsonl
{"n":1,"k":"DictKey_ActionText_105","t":"Свяжитесь с NELLIS DEPARTURE."}
{"n":2,"k":"DictKey_ActionText_106","t":"Дозаправка завершена, свяжитесь с DARKSTAR."}
```

То adapter может собрать текст в уже понятный `CompactNumbered` формат:

```csharp
static string ConvertAgentJsonlToCompactNumberedText(
    string batchId,
    IEnumerable<AgentTranslationLine> lines)
{
    var builder = new StringBuilder();
    builder.Append("Batch: ").AppendLine(batchId);

    foreach (var line in lines.OrderBy(line => line.Number))
    {
        builder
            .Append(line.Number.ToString(CultureInfo.InvariantCulture))
            .Append('»')
            .Append(' ')
            .AppendLine(line.Translation);
    }

    return builder.ToString().TrimEnd();
}
```

Дальше:

```csharp
var text = ConvertAgentJsonlToCompactNumberedText(run.Manifest.BatchId, agentLines);

var plan = TranslationBatchDocument.Analyze(
    text,
    run.Manifest,
    overwriteExisting: true,
    targetLocale: settings.TargetLanguage);
```

Так мы переиспользуем существующий parser/preview/apply и не плодим второй путь применения переводов.

#### Где лучше не делать новые изменения

Не стоит добавлять ещё 1000 строк в `MainWindow.xaml.cs`. Он уже является главным узлом. Для автоперевода лучше:

```text
MainWindow.xaml.cs:
  только event handlers и wiring

Services/AiTranslationPipeline.cs:
  orchestration

Services/AgentRunStore.cs:
  files/run folder

Services/McpServerHost.cs:
  process lifecycle

Services/McpAgentClient.cs:
  protocol/API calls

Views/AiTranslationSettingsWindow.xaml:
  settings UI

Views/AiTranslationRunWindow.xaml:
  progress/cancel/retry/open folder
```

#### Минимальная точка интеграции UI

В `TranslationWorkspace.xaml` можно добавить новую кнопку рядом с текущими AI controls:

```xml
<Button x:Name="AiAgentTranslateVisibleButton"
        Content="AI-агент: перевести видимые"
        Click="AiAgentTranslateVisible_Click"/>
```

В `TranslationWorkspace.xaml.cs` только событие:

```csharp
public event RoutedEventHandler? AiAgentTranslateVisibleRequested;

private void AiAgentTranslateVisible_Click(object sender, RoutedEventArgs e)
    => AiAgentTranslateVisibleRequested?.Invoke(this, e);
```

В `MainWindow.xaml.cs`:

```csharp
TranslationWorkspaceView.AiAgentTranslateVisibleRequested +=
    async (_, _) => await RunAiAgentTranslationForVisibleAsync();
```

А реальная логика должна уйти в service:

```csharp
private async Task RunAiAgentTranslationForVisibleAsync()
{
    var entries = GetEditableVisibleTranslationEntries();
    var scope = AiTranslationScope.FromEntries(entries);

    var run = await _aiTranslationPipeline.ExportAsync(
        scope,
        _aiSettings,
        CancellationToken.None);

    run = await _aiTranslationPipeline.TranslateAsync(
        run,
        _aiAgentClient,
        CancellationToken.None);

    var plan = await _aiTranslationPipeline.BuildPreviewPlanAsync(
        run,
        overwriteExisting: AiOverwriteExistingBox.IsChecked == true,
        targetLocale: _translationLocale ?? CurrentLocale,
        CancellationToken.None);

    if (!TranslationWorkspaceView.ShowImportPreview(plan))
        return;

    RunTranslationBulkUpdate(() => TranslationBatchDocument.Apply(plan));
}
```

Это псевдокод, но направление правильное: `MainWindow` вызывает pipeline, а не содержит его внутри себя.

#### Текущий CLI-прототип, который можно превратить в сервис

`tests/F4E.Translation/Program.cs` уже умеет:

```text
--agent-export:
  .miz -> folder/chunk-001.jsonl ... manifest.json

--agent-import:
  translated/chunk-*.jsonl -> RU locale -> output .miz
```

`scripts/Translate-F4ETraining.ps1` уже показывает рабочий внешний pipeline:

```text
export training missions
agents fill translated/chunk-*.jsonl
import translations
validate with GreenLine.Integration
backup originals
replace originals if requested
write report
```

Для продукта это надо перенести из scripts/tests в нормальные services:

```text
F4E.Translation agent-export/import
  -> Services/AgentRunStore
  -> Services/AiTranslationPipeline
  -> Services/AgentChunkValidator
  -> UI preview/apply
```

#### Что показать другой нейронке как “не ломать”

```text
Do not remove legacy input support:
  - [[N]]
  - 🔹N🔹
  - N.
  - N)
  - [[M2|batch|number]]
  - [[MZ1|base64key|part]]

Do not expose DictKey/M2/MZ in normal user copy:
  - normal copy should be Batch + 1»

Do not bypass:
  - TranslationBatchDocument.Analyze
  - TranslationImportPreviewWindow
  - TranslationBatchDocument.Apply
  - fingerprint stale checks

Do not write .miz from MCP/agent.
MizEdit must remain the only writer.
```

#### Suggested first code PR boundaries

Чтобы код-агенты не конфликтовали, лучше не давать всем править `MainWindow.xaml.cs` одновременно.

Первый PR — только контракты:

```text
Core/AiTranslationContracts.cs
Services/IAiSecretStore.cs
Services/IAiAgentClient.cs
Services/IMcpServerHost.cs
```

Второй PR — settings/secret storage:

```text
Services/AiTranslationSettingsStore.cs
Services/DpapiSecretStore.cs
Views/AiTranslationSettingsWindow.xaml(.cs)
```

Третий PR — run folder/validator:

```text
Services/AgentRunStore.cs
Services/AgentChunkValidator.cs
tests/GreenLine.Integration additions
```

Четвёртый PR — pipeline:

```text
Services/AiTranslationPipeline.cs
Views/AiTranslationRunWindow.xaml(.cs)
minimal MainWindow wiring
```

Пятый PR — MCP host:

```text
Services/McpServerHost.cs
Services/McpAgentClient.cs
mizedit.csproj publish items
```

Только после этого — folder translation UI.

### Главный принцип безопасности

Нужно жёстко разделить ответственность:

```text
AI agent / MCP server:
  - получает только translation chunks;
  - возвращает только перевод;
  - не получает право писать .miz;
  - не получает право удалять/заменять ресурсы архива.

MizEdit:
  - экспортирует manifest/chunks;
  - валидирует ответ;
  - показывает preview;
  - применяет перевод к TranslationEntry;
  - делает backup;
  - сохраняет .miz;
  - проверяет reopen/validation.
```

Это самый важный пункт. Если дать агенту напрямую писать `.miz`, потом будет почти невозможно понять, кто сломал mission-файл: модель, MCP, архиватор, Lua-сериализация или сам MizEdit.

### Как именно включать AI-агентов в софте

В интерфейсе нужен отдельный верхний пункт меню:

```text
AI агенты
  Включить AI-агентов
  Настройки AI...
  Проверить подключение
  Перевести текущую миссию...
  Перевести выбранные строки...
  Перевести все пропущенные...
  Перевести папку миссий...
  Экспортировать задачу для внешних агентов...
  Импортировать результат внешних агентов...
  Открыть папку текущего задания
  Открыть логи
  Остановить перевод
```

Почему отдельное меню лучше, чем просто ещё одна кнопка:

- пользователь явно понимает, что это отдельный “режим”;
- проще добавить MCP path/API keys без перегруза основного экрана;
- проще показать cloud/local предупреждения;
- можно оставить старый ручной copy/paste workflow как fallback;
- можно позже добавить перевод папки миссий без хаоса в `TranslationWorkspace`.

### Настройки, которые точно нужны в первой версии

```text
AI agents:
  [x] Enable AI agents in MizEdit

Provider:
  Default provider:
    - Bundled MCP server
    - External MCP executable
    - External MCP URL
    - Gemini
    - Ollama/local
    - Manual external agents

MCP:
  MCP mode: Bundled / Custom path / URL
  MCP executable path: C:\...\mizedit-agent-server.exe
  MCP URL: http://127.0.0.1:8765
  [Test connection]
  [Start with MizEdit]
  [Stop when MizEdit closes]

API keys:
  Gemini API key: ******** [Set] [Test] [Delete]
  OpenAI-compatible key: ******** [Set] [Test] [Delete]
  OpenRouter key: ******** [Set] [Test] [Delete]

Translation:
  Target language: RU
  Chunk size: 30 default
  Parallel chunks: 1-4
  Retries: 2
  Deduplicate exact repeats: on
  Stop at preview: always on in MVP
  Preserve DCS terms: always on

Folders:
  Work folder
  Logs folder
  Backup folder
```

API keys нельзя хранить в `translation_tool_settings.json` открытым текстом. Минимально нормальный вариант для Windows:

- обычные настройки: JSON в `%AppData%\MizEdit\`;
- секреты: DPAPI через `ProtectedData`;
- позже лучше Windows Credential Manager.

В логах и отчётах ключей быть не должно вообще.

### Как “прилепить MCP server к билду”

Я бы заложил три режима.

#### Режим 1: bundled

Папка билда:

```text
MizEdit.exe
MizEdit.dll
tools/
  mizedit-agent-server/
    mizedit-agent-server.exe
    config.example.json
    README.md
```

MizEdit при старте или при открытии AI settings проверяет:

- существует ли bundled server;
- подходит ли версия;
- отвечает ли health endpoint;
- можно ли его запустить как child process.

Health response:

```json
{
  "name": "mizedit-agent-server",
  "version": "0.1.0",
  "protocol": "mizedit.translation.v1",
  "providers": ["gemini", "openai-compatible", "manual"],
  "status": "ready"
}
```

#### Режим 2: custom path

Пользователь указывает свой сервер:

```text
C:\path\to\llm-workers-mcp\server.exe
```

MizEdit запускает его, но не пытается обновлять или менять.

#### Режим 3: external URL

Пользователь сам запустил MCP/server где-то отдельно:

```text
http://127.0.0.1:8765
```

MizEdit только проверяет `/health` и отправляет задания.

### Почему нужен job/run folder

Для больших миссий нельзя держать всё только в памяти. Нужна папка задания, которую можно открыть, проверить, продолжить, отправить другому агенту.

Рекомендуемый формат:

```text
%AppData%\MizEdit\agent-runs\run-2026-08-23-014500\
  run.json
  manifest.json
  settings.snapshot.json
  source\
    original.miz
  chunks\
    chunk-001.jsonl
    chunk-002.jsonl
  translated\
    chunk-001.jsonl
    chunk-002.jsonl
  preview\
    import-plan.json
    report.md
  output\
    translated.miz
  backup\
    original.miz
  logs\
    job-events.jsonl
    mcp-server.log
    provider-redacted.log
```

`settings.snapshot.json` должен фиксировать:

- версию MizEdit;
- версию batch protocol;
- provider/model;
- chunk size;
- target language;
- mission hash;
- source locale;
- dictionary fingerprint.

Но не API keys.

### Компактный формат для агентного API

Для UI copy/paste мы уже выбрали `Batch + 1»`.

Для внутреннего agent/MCP API можно сделать ещё компактнее, потому что MizEdit уже хранит source в manifest:

```jsonl
{"n":1,"k":"DictKey_ActionText_105","t":"Свяжитесь с NELLIS DEPARTURE."}
{"n":2,"k":"DictKey_ActionText_106","t":"Дозаправка завершена, свяжитесь с DARKSTAR."}
```

Плюсы:

- меньше токенов;
- агенту не надо эхоить весь `Source`;
- проще валидировать;
- меньше шанс, что агент случайно изменит source.

Минус:

- для ручной отладки человеку хуже видно исходник.

Компромисс:

- в `chunks/source-full/` хранить полный JSONL с `Source`;
- агенту разрешить возвращать компактный JSONL `n/k/t`;
- валидатор мержит по `n + k`;
- для debug report можно показывать source из manifest.

### Validator must-have

Перед preview валидатор обязан проверить:

- JSON/JSONL парсится;
- файл относится к текущему `runId`;
- `protocolVersion` совпадает;
- `n` существует;
- `k` совпадает;
- нет дублей с конфликтующим переводом;
- нет пропущенных строк, если режим требует полный перевод;
- `Translation/t` не пустой;
- нет Markdown fences;
- не повреждены `%s`, `{0}`, `{name}`, `\n`, `\\n`;
- не повреждены частоты, координаты, headings, BRA/BRAA;
- не повреждены имена ресурсов/файлов;
- не появился `DictKey_` внутри перевода, если это не исходный текст;
- Lua/technical строки не попали в перевод;
- suspicious length подсвечивается;
- likely untranslated английские строки подсвечиваются для RU.

Важно: не всё должно быть fatal. Например suspicious length — warning. А вот key mismatch/wrong runId — fatal.

### Preview для agent run

Preview должен быть не “маленькая формальность”, а главный экран доверия.

Нужные колонки:

- номер;
- DictKey только в деталях/advanced;
- source;
- current translation;
- proposed translation;
- status;
- warnings;
- provider/chunk id.

Сводка:

```text
Accepted: 1840
Unchanged: 23
Missing: 5
Rejected: 2
Warnings: 48
Deduplicated aliases: 312
Estimated changed .miz files: dictionary, mapResource
```

Кнопки:

- `Apply accepted`;
- `Retry rejected`;
- `Export rejected chunk`;
- `Cancel`;
- `Open run folder`.

В MVP кнопка `Apply` должна быть недоступна при fatal errors.

### Resume/retry

Это нужно сразу, иначе большие обучалки будут мучением.

Правила:

- валидный `translated/chunk-XXX.jsonl` не переводить повторно;
- битый chunk помечать rejected;
- `Retry failed chunks` переводит только failed/missing;
- если source mission hash поменялся — запретить apply;
- если settings поменялись — разрешить continue, но записать warning в report;
- если пользователь остановил job — папка остаётся, можно продолжить.

### Cost/token estimate

Перед запуском показать грубую оценку:

```text
Export rows: 9123
Unique rows after dedup: 7340
Approx input chars: 680k
Approx output chars: 760k
Estimated chunks: 245
Provider: Gemini/OpenAI/MCP
Mode: cloud/local
```

Для токенов можно грубо считать:

```text
english_tokens ≈ chars / 4
russian_tokens ≈ chars / 3
json_overhead ≈ rows * 10-25 tokens
```

Отдельно показывать экономию dedup:

```text
Dedup saved: 1783 repeated rows
Estimated token reduction: ~18%
```

### Hidden bugs, которые нужно прямо искать тестами

1. Агент вернул валидный JSONL, но от другой миссии.
2. Агент поменял `Key`, но визуально перевод нормальный.
3. Агент потерял одну строку в середине 1000 строк.
4. Агент объединил две соседние строки.
5. Агент добавил Markdown вокруг JSONL.
6. Агент перевёл cockpit label, который должен совпадать с DCS.
7. Агент поменял `F10`, `UHF`, `TACAN`, `JESTER`, `WSO`, `INS`.
8. Агент поменял `127.500`, `030`, `N 42 12.000`.
9. Агент сломал `%s`, `{0}`, `{playerName}`, `\n`.
10. Агент сделал перевод в другом стиле после retry.
11. Два параллельных job переводят одну миссию.
12. DCS держит `.miz` заблокированным.
13. MCP server завис без завершения процесса.
14. API quota закончилась на середине job.
15. Пользователь закрыл MizEdit во время записи backup.
16. Windows Defender заблокировал bundled server.
17. Путь к MCP или миссии содержит пробелы/кириллицу.
18. API key попал в exception message или log.
19. Пользователь импортирует старые chunks после изменения фильтров.
20. Дедуп применил один перевод к строкам, где нужен разный контекст.

### Что я советую делать первым

Я бы не начинал с bundled MCP. Сначала лучше сделать основу в MizEdit:

```text
Phase 0:
  - зафиксировать DTO/settings/job contracts;
  - не трогать UI глубоко;
  - добавить тесты validator/run folder.

Phase 1:
  - AI Settings window;
  - Enable AI agents toggle;
  - Provider selector;
  - DPAPI secret store;
  - Test connection button.

Phase 2:
  - Agent run folder export/import из UI;
  - manual external agents mode;
  - preview/apply через текущий TranslationImportPreviewWindow.

Phase 3:
  - встроенный provider client;
  - сначала Gemini/OpenAI-compatible или existing Gemini adapter;
  - retry/resume по chunks.

Phase 4:
  - MCP server host;
  - custom path + external URL;
  - только потом bundled MCP в build zip.

Phase 5:
  - folder translation;
  - batch reports;
  - replace originals только после явного подтверждения.
```

Почему так:

- manual run-folder уже доказан на F-4E Red Flag/Training;
- меньше риска сломать основной редактор;
- можно тестировать качество перевода до сложной MCP-упаковки;
- API keys/security лучше сделать до cloud-интеграции.

### Как раздать задачи код-агентам после утверждения

#### Агент 1: Contracts & Settings

Файлы:

- `Core/AiTranslationSettings.cs`
- `Services/AiTranslationSettingsStore.cs`
- `Services/IAiSecretStore.cs`
- `Services/DpapiSecretStore.cs`

Задача:

- создать DTO настроек;
- хранить обычные настройки в `%AppData%`;
- секреты через DPAPI;
- unit/integration tests.

#### Агент 2: Run Folder & Validator

Файлы:

- `Core/AiTranslationRun.cs`
- `Services/AgentRunStore.cs`
- `Services/AgentChunkValidator.cs`

Задача:

- создать структуру run folder;
- экспортировать chunks;
- импортировать compact `n/k/t`;
- валидировать mismatch/missing/duplicate/fences.

#### Агент 3: UI Settings

Файлы:

- `Views/AiTranslationSettingsWindow.xaml`
- `Views/AiTranslationSettingsWindow.xaml.cs`
- `Resources/Strings.ru.xaml`
- `Resources/Strings.en.xaml`

Задача:

- отдельное окно настроек AI;
- enable agents;
- MCP path/URL;
- API key masked input;
- test connection.

#### Агент 4: Pipeline & Preview

Файлы:

- `Services/AiTranslationPipeline.cs`
- `Services/IAiAgentClient.cs`
- `Views/TranslationWorkspace.xaml(.cs)`
- `Views/TranslationImportPreviewWindow.xaml(.cs)`

Задача:

- кнопка `Перевести через AI-агента`;
- export → provider → validate → preview;
- apply accepted only;
- progress/cancel/retry.

#### Агент 5: MCP Host & Packaging

Файлы:

- `Services/McpServerHost.cs`
- `mizedit.csproj`
- publish scripts

Задача:

- bundled/custom/url modes;
- health/version check;
- child process lifecycle;
- include `tools/mizedit-agent-server` in build zip;
- no API keys in command line/logs.

#### Агент 6: Regression

Файлы:

- `tests/GreenLine.Integration/Program.cs`
- new test fixtures where possible

Задача:

- тесты on validator;
- stale mission hash;
- wrong run id;
- missing chunk;
- duplicate conflict;
- protected tokens;
- DCS training mission smoke-test.

### Мой финальный совет

Делать эту функцию стоит. Она очень подходит MizEdit, потому что реальные DCS кампании — это тысячи строк, и ручной copy/paste быстро превращается в болото.

Но я бы держал философию такой:

```text
AI is a translator, not an editor of mission archives.
MizEdit is the authority that validates, previews, applies and saves.
```

По-русски: нейронка пусть переводит, но руль и тормоза должны оставаться у MizEdit и пользователя.

## Дополнение: почему переведённые `.miz` могут не стартовать в DCS

### Наблюдение из F-4E Red Flag

После автоматического перевода F-4E Red Flag оригинальная миссия имела только:

```text
l10n/DEFAULT/...
```

А переведённая версия получила полноценную новую локаль:

```text
l10n/DEFAULT/...
l10n/RU/...
```

В `l10n/RU` были скопированы не только `dictionary` и `mapResource`, но и все картинки, звуки и Lua-ресурсы. По свежему `dcs.log` DCS споткнулся не на распаковке архива, а в GUI/Mission Editor цепочке:

```text
Scripts/dictionary.lua:getLangs
MissionEditor/modules/me_statusbar.lua:updateLang
MissionEditor/modules/me_mission.lua:load
Mods/aircraft/F-4E/.../command_defs.lua
```

Рабочая гипотеза: для части официальных/модульных миссий DCS некорректно реагирует на добавленную пользовательскую локаль `RU`, особенно когда внутри неё дублируются Lua-ресурсы. Поэтому “создать новую локаль и скопировать туда всё” нельзя считать безопасным универсальным режимом.

### Новый безопасный режим сохранения

Добавить режим `Single-locale patch`:

- не создавать `l10n/RU`;
- не добавлять новый язык в миссию;
- сохранять перевод прямо в `l10n/DEFAULT/dictionary` внутри копии `.miz`;
- не трогать `mission`, `warehouses`, `options` и прочие backend-файлы без явной необходимости;
- ресурсы `.ogg`, `.jpg`, `.lua` не копировать между локалями;
- перед сохранением показывать diff: изменён только `l10n/DEFAULT/dictionary` или есть неожиданные изменения.

Этот режим должен стать дефолтом для:

- официальных модульных миссий;
- paid/DRM campaigns;
- F-4E/Reflected/Sedlo/Heatblur-style missions;
- любого `.miz`, где есть external loader/protection metadata.

Новая локаль `l10n/RU` может остаться отдельной advanced-опцией, но только с preview и предупреждением, что DCS может не показать или не загрузить миссию.

### Защита служебных loader-полей

При чтении `mission` MizEdit должен обнаруживать технические поля вроде:

```lua
["ext_loader"] = {
    ["library"] = "...",
    ["miz_id"] = "..."
}
```

Правила по умолчанию:

- не отправлять такие значения в AI-перевод;
- не показывать секретоподобные значения открытым текстом в UI/log/report;
- не изменять их при переводе;
- при сохранении валидировать, что они остались байт-в-байт или структурно неизменными;
- если AI/импорт поменял loader-поле — блокировать apply/save как опасное изменение.

### Легальная очистка для собственных миссий

Для авторов своих миссий можно добавить отдельный инструмент `Mission Metadata / Privacy Cleanup`.

Важно: это не режим обхода защиты чужих платных кампаний. UI должен прямо говорить: использовать только для своих миссий или при наличии разрешения автора.

Режимы:

1. `Protect during translation` — дефолт, ничего не удаляет.
2. `Clean into copy` — создаёт новую копию `.miz` с удалёнными выбранными metadata-полями.
3. `Clean current mission` — advanced-режим только после backup и явного подтверждения.

Удаление делать не regex-поиском, а через Lua-структуру:

- распарсить `mission`;
- удалить выбранный узел, например `mission.ext_loader`;
- сериализовать обратно;
- проверить, что основные mission-разделы сохранились.

Минимальная проверка после cleanup:

- `.miz` открывается как zip;
- файл `mission` парсится;
- есть `coalition`, `triggers`, `theatre`, `requiredModules` если они были раньше;
- количество units/triggers/resources не изменилось;
- изменены только выбранные metadata-поля;
- `l10n` структура не изменилась без явного выбора пользователя.

### Инвариант для автоперевода

Автоперевод не должен быть архивным редактором. Правильная модель:

```text
AI changes text values only.
MizEdit owns archive structure, mission metadata, validation and save.
```

Если после перевода меняется что-то кроме ожидаемых dictionary/mapResource значений, preview должен показать это красным до сохранения.
