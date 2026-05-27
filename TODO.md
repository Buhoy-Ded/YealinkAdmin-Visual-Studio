# TODO

План работ по YealinkAdmin. Приоритеты идут сверху вниз.

## P0 - сейчас

- Перезапустить приложение после последнего изменения modern login-flow.
- Проверить статус на `10.6.10.42` (T43U, build `107.1.0.0.3.1.0`).
- Если ошибка останется, взять полный текст новой вложенной ошибки из UI/логов.
- Не убивать чужой запущенный процесс без явного согласия; если EXE блокирует build, собирать в `.codex-build`.

## P0 - T4xx/T5xx status

- Довести `YealinkStatusClient` для modern API:
  - `POST /api/common/info?p=Login` с RSA keys;
  - `POST /api/auth/login?p=Login` с `username` и RSA `pwd`;
  - warmup `POST /api/common/info?p=Login` с `idlist:["wui"]`;
  - `POST /api/common/info?p=StatusGeneral` с idlists из HAR.
- Проверить на:
  - `10.6.10.42` T43U / build `107.1.0.0.3.1.0`;
  - `10.6.10.66` T46U / build `108.1.0.0.3.1.0`;
  - `10.6.10.75` T57W / build `97.1.2.0.2.1.2`;
  - `10.6.10.70` W70B / build `146.0.0.0.2.0.0`.
- Нормализовать поля modern status в те же UI-ключи, что и T30P:
  - firmware;
  - build;
  - MAC;
  - serial/device id;
  - IPv4;
  - gateway;
  - DNS;
  - WAN/PC port status;
  - uptime/current time;
  - account registration.

## P0 - безопасность

- Не хранить admin-креды телефонов на диске.
- Не коммитить HAR с cookies/tokens/паролями.
- Если в коде или истории снова всплывет секретоподобная строка, считать ее скомпрометированной.
- В логах не печатать `pwd`, `password`, `Cookie`, auth/session tokens.

## P1 - сканирование

- При сканировании проверять креды на реальном телефоне и явно показывать ошибку `Login failed`, если креды неверные.
- Для good IP сразу сохранять данные списка и статус в `phones.json`, чтобы вкладка status открывалась без повторного запроса.
- В списке держать колонки:
  - IP;
  - модель;
  - аккаунт;
  - MAC;
  - серийник;
  - состояние;
  - last seen.
- Для `403 Forbidden` добавлять телефон в список с `IsForbidden = true`.
- Не падать всем сканированием, если один IP не отвечает или не поддерживает endpoint.

## P1 - модель телефона

- Основной resolver должен работать по build version.
- Текущие mappings:
  - `107` -> `SIP-T43U`;
  - `108` -> `SIP-T46U`;
  - `97` -> `SIP-T57W`;
  - `127` -> `SIP-T30P`;
  - `146` -> `SIP-W70B`.
- Firmware version оставить только fallback.
- Если модель неизвестна, показывать `Unknown build (...)`, а не модельный ряд по firmware.

## P1 - account config UI

- Для T30P оставить рабочую схему.
- Для T43U/T46U использовать ключи:
  - `AccountServerAddr1.1`;
  - `AccountServerPort1.1`;
  - `AccountServerTransport1.1`;
  - `AccountServerExpires1.1`;
  - `AccountServerRetryCounts1.1`.
- Для T57W использовать ключи:
  - `AccountServerAddr.1.1`;
  - `AccountServerPort.1.1`;
  - `AccountServerTransport.1.1`;
  - `AccountServerExpires.1.1`;
  - `AccountServerRetryCounts.1.1`.
- Кнопка `Загрузить из телефона` должна подтягивать account settings в форму.
- Пароль из телефона не подставлять в UI автоматически.

## P1 - config export/import

- Реализовать modern export:
  - `POST /api/diagnosis/cfg/file?action=export&type=all`.
- Реализовать modern import:
  - `POST /api/diagnosis/cfg/file?action=import`.
- Проверить нужен ли `csrfmiddlewaretoken` как form field для конкретных моделей.
- Сохранять выбранный `.cfg` локально только по явному действию пользователя.
- Сделать понятные сообщения:
  - export success;
  - import success;
  - phone rejected config;
  - reboot/apply required.

## P2 - UI polish

- Исправить mojibake в русских строках, если он виден в `.razor` файлах.
- Проверить прокрутку на маленьких экранах и при длинных таблицах.
- Не делать marketing/landing экран; первый экран должен быть рабочей консолью.
- Добавить устойчивые empty/error/loading states.
- Для 403 строк использовать понятную желтую подсветку и текст "требуется настроить Action URI".

## P2 - network/VPN

- Документировать рабочий способ обхода VPN для подсети телефонов.
- Предпочтительно route-based split tunneling:

```powershell
route -p add 10.6.10.0 mask 255.255.255.0 <local_gateway> metric 1
```

- Проверить, не запрещает ли корпоративный VPN split tunneling.
- Не вносить route changes автоматически без согласия пользователя.

## P2 - тесты и диагностика

- Добавить unit-тесты для:
  - `ModelResolver`;
  - HTML parser T30P;
  - JSON flattening modern API;
  - account key mapping T43/T46 vs T57.
- Добавить диагностический режим для sanitized request/response:
  - endpoint;
  - method;
  - status code;
  - content type;
  - короткий redacted body snippet.
- Не логировать секреты.

## HAR references

Локальные HAR, использованные для reverse engineering:

```text
C:\Users\d.karapetyan\Downloads\10.6.10.42.har - T43U, build 107.1.0.0.3.1.0
C:\Users\d.karapetyan\Downloads\10.6.10.66.har - T46U, build 108.1.0.0.3.1.0
C:\Users\d.karapetyan\Downloads\10.6.10.75.har - T57W, build 97.1.2.0.2.1.2
C:\Users\d.karapetyan\Downloads\10.6.10.70.har - W70B, build 146.0.0.0.2.0.0
```

Перед любым выводом данных из HAR надо редактировать секреты.
