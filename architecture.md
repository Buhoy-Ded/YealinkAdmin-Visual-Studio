# Architecture

Документ фиксирует текущее состояние проекта, чтобы не восстанавливать контекст из переписки.

## Общая идея

`YealinkAdmin` - локальное Blazor Server приложение для работы с Yealink-телефонами в LAN.

Основные принципы:

- UI и backend живут в одном локальном ASP.NET Core процессе;
- телефоны опрашиваются напрямую с машины оператора;
- admin-креды телефонов хранятся только в памяти процесса;
- состояние найденных телефонов кешируется в JSON;
- T30P и T4xx/T5xx/W70B обрабатываются разными web-flow, потому что API реально разные.

## Структура проекта

```text
Api/
  ProxyController.cs
  ScanController.cs

Components/
  App.razor
  Routes.razor
  Layout/
    MainLayout.razor
  Pages/
    AdminLogin.razor
    PhoneList.razor
    PhoneDetail.razor

Models/
  PhoneInfo.cs
  PhoneConfig.cs
  PhoneStatus.cs

Services/
  Core/
    YealinkApiClient.cs
    YealinkParser.cs
    YealinkScanner.cs
    ModelResolver.cs
  Web/
    YealinkWebClient.cs
    YealinkStatusClient.cs
    YealinkConfigManager.cs
  Infrastructure/
    PhoneStore.cs
    SecureCredentialStorage.cs
    TemplateEngine.cs

wwwroot/
  app.css
```

## Dependency injection

Регистрация находится в `Program.cs`.

Основные сервисы:

- `SecureCredentialStorage` - временное хранение admin-кредов телефонов.
- `PhoneStore` - JSON-хранилище списка телефонов.
- `YealinkApiClient` - легкий `phonecfg`-опрос при сканировании.
- `YealinkScanner` - обход IP-диапазона и сбор данных.
- `YealinkStatusClient` - статус и параметры аккаунта через web/API телефонов.
- `YealinkConfigManager` - download/upload/apply config.
- `YealinkWebClient` - web-взаимодействие с телефонами.

HTTP clients:

- `yealink` - короткие запросы сканирования, timeout 10 секунд.
- `yealink-web` - web/config операции, timeout 30 секунд.

Для телефонов используется permissive TLS validation, потому что у Yealink часто самоподписанные сертификаты.

## Data model

`PhoneInfo`:

```csharp
public string IpAddress { get; set; }
public string MacAddress { get; set; }
public string SerialNumber { get; set; }
public string Account { get; set; }
public string? Model { get; set; }
public bool IsOnline { get; set; }
public DateTime LastSeen { get; set; }
public bool IsForbidden { get; set; }
public Dictionary<string, string> StatusFields { get; set; }
```

`StatusFields` - кеш нормализованных данных статуса по конкретному IP.

В `PhoneInfo` нельзя добавлять пароль или session tokens.

## Storage

`PhoneStore` хранит данные в `phones.json` рядом с EXE:

```csharp
Path.Combine(AppContext.BaseDirectory, "phones.json")
```

При сканировании список может очищаться и наполняться заново.

## Admin mode

Отдельной авторизации приложения сейчас нет. Есть только admin-режим для доступа к телефонам.

`SecureCredentialStorage` держит:

- username;
- password;
- время истечения сессии.

Это специально не пишется на диск.

## Scanning flow

Сканирование делает `YealinkScanner`.

Базовый порядок:

1. Сформировать список IP из подсети.
2. Для каждого IP вызвать `YealinkApiClient.QueryDetailedAsync`.
3. Если `200 OK`, распарсить `phonecfg` через `YealinkParser`.
4. Если endpoint unsupported (`404`, `400`, `405`), попробовать получить данные через `YealinkStatusClient`.
5. Если `403 Forbidden`, добавить телефон с `IsForbidden = true`.
6. Для найденного телефона попытаться обогатить данные статусом.
7. Сохранить результат в `PhoneStore`.

Важно: `403 Forbidden` означает не "выкинуть IP", а "телефон найден, нужна настройка Action URI".

## Model resolving

Модель определяется через `ModelResolver`.

Приоритет:

1. Явное поле `Product Name` / `Model` из статуса.
2. Первый номер версии сборки.
3. Первый номер firmware version как fallback.

Текущие build mappings:

```text
107 -> SIP-T43U
108 -> SIP-T46U
97  -> SIP-T57W
127 -> SIP-T30P
```

Firmware fallback:

```text
124 -> SIP-T33P/T33G/T31P/T31G/T31/T30P/T30
108 -> SIP-T48U/T46U/T43U/T42U
96  -> SIP-T57W
146 -> SIP-W70B
```

Решение: модель лучше определять по build version, потому что одна версия ПО может относиться к нескольким моделям.

## Status: T30P legacy servlet

Рабочий flow:

```text
GET  /servlet?m=mod_listener&p=login&q=loginForm&Random=...
POST /servlet?m=mod_listener&p=login&q=login&Rajax=...
GET  /servlet?m=mod_data&p=status&q=load
```

Особенности:

- RSA ключи лежат в HTML как `g_rsa_n` и `g_rsa_e`.
- Пароль шифруется RSA PKCS#1.
- Статус приходит HTML/JS, парсится regex-парсером.
- Account status берется из `g_dataAccStatus`.
- `Hardware Version` используется как `Build Version`.

## Status: T4xx/T5xx/W70B modern API

HAR показал такой flow:

```text
POST /api/common/info?p=Login&t=...
body: {"idlist":["wui.common.rsaN","wui.common.rsaE"]}

POST /api/auth/login?p=Login&t=...
form: username=<admin>&pwd=<rsa encrypted password>

POST /api/common/info?p=Login&t=...
body: {"idlist":["wui"]}

POST /api/common/info?p=StatusGeneral&t=...
body: {"idlist":["wui"]}

POST /api/common/info?p=StatusGeneral&t=...
body: {"idlist":["system","network","cert","account.info","dsskey.expkey.list","dsskey.ehs40","accessory.mic_info"]}
```

Для T57W часть данных может приходить через более короткий набор:

```text
["system","network","cert"]
```

Критичные детали:

- modern API идет по HTTPS;
- нужно отправлять `Content-Type: application/json;charset=UTF-8`;
- login form field называется `username`, не `user`;
- пароль шифруется RSA через ключи из `/api/common/info?p=Login`;
- полезно повторять browser headers: `Accept`, `Origin`, `Referer`, `User-Agent`.

## Account config: T4xx/T5xx

Страница настроек аккаунта использует:

```text
POST /api/common/info?p=AccountRegister&t=...
body: {"idlist":["wui"]}

POST /api/inner/readconfig?p=AccountRegister&t=...
body: {"formData":[...]}

GET /api/account/info?type=sip&p=AccountRegister&t=...
```

Разные семейства используют разные имена ключей.

T43U/T46U:

```text
AccountServerAddr1.1
AccountServerPort1.1
AccountServerTransport1.1
AccountServerExpires1.1
AccountServerRetryCounts1.1
```

T57W:

```text
AccountServerAddr.1.1
AccountServerPort.1.1
AccountServerTransport.1.1
AccountServerExpires.1.1
AccountServerRetryCounts.1.1
```

`PhoneDetail.razor` должен поддерживать оба варианта.

## Config import/export

По HAR для modern API:

Export:

```text
POST /api/diagnosis/cfg/file?action=export&type=all
```

Import:

```text
POST /api/diagnosis/cfg/file?action=import
```

Связанные status/readconfig endpoints:

```text
GET  /api/diagnosis/status?type=all&p=SettingConfig&t=...
POST /api/inner/readconfig?p=SettingConfig&t=...
GET  /api/autop/status?noAutoJump=true&atptaskid=0&p=SettingConfig&t=...
```

Это еще нужно полноценно довести в коде для T4xx/T5xx/W70B.

## UI

Главные страницы:

- `PhoneList.razor` - список телефонов, сканирование, быстрый переход в карточку.
- `PhoneDetail.razor` - status/config вкладки по конкретному IP.
- `AdminLogin.razor` - ввод admin-кредов телефонов.

UI-идея:

- рабочая консоль, не landing page;
- плотные таблицы и формы;
- статус кешируется и показывается сразу, если уже есть в `phones.json`;
- ручное обновление статуса делает живой запрос к телефону.

## Known issues

- VPN может перехватывать маршрут до подсети телефонов.
- Запущенный EXE блокирует обычную сборку в `bin`.
- HAR может содержать cookies/tokens/учетки; при анализе не печатать секреты.
- Modern API пока тестируется: если статус T4/T5 падает, первым делом смотреть вложенную ошибку.
- UI/код местами содержит mojibake в русских строках из-за старой кодировки; при касании файла лучше аккуратно привести строки к нормальному UTF-8.
