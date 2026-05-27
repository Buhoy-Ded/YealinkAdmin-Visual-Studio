# YealinkAdmin

Локальная админ-панель для управления IP-телефонами Yealink в корпоративной сети.
Приложение запускается как локальный ASP.NET Core / Blazor Server UI и работает с телефонами напрямую по HTTP/HTTPS.

## Цель проекта

Сделать удобную локальную консоль для:

- сканирования подсетей и поиска Yealink-телефонов;
- проверки admin-кредов телефонов;
- сохранения списка найденных телефонов в JSON;
- просмотра статуса телефона без постоянного повторного вытягивания данных;
- выгрузки, редактирования и загрузки конфигурации `.cfg`;
- отдельной обработки старых T30P и новых T4xx/T5xx/W70B, потому что у них разные web/API-механизмы.

## Текущий стек

- .NET 8
- ASP.NET Core
- Blazor Server / InteractiveServer
- Windows x64
- локальный single-file/self-contained EXE в publish-сценарии

Проект намеренно не WASM: published single-file EXE с WASM уже давал проблемы со статикой и неработающими кнопками.

## Запуск для разработки

```powershell
dotnet run
```

По умолчанию приложение слушает:

```text
http://localhost:5000
```

Если уже запущен `YealinkAdmin.exe`, обычная сборка может упасть на копировании exe:

```text
The process cannot access the file YealinkAdmin.exe because it is being used by another process.
```

Для проверки компиляции без остановки запущенного приложения можно использовать отдельный output:

```powershell
dotnet build --no-restore -o .codex-build
```

## Publish

В `YealinkAdmin.csproj` сейчас включено:

```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
```

Ожидаемый формат доставки: локальный Windows EXE.

## Данные и безопасность

Список телефонов хранится в:

```text
phones.json
```

Файл создается рядом с исполняемым приложением, через `AppContext.BaseDirectory`.

В `phones.json` можно хранить:

- IP;
- MAC;
- серийник;
- модель;
- аккаунт/внутренний номер;
- online/offline;
- флаг `IsForbidden`;
- кеш полей статуса `StatusFields`.

В `phones.json` нельзя хранить:

- admin-логин;
- admin-пароль;
- cookies;
- session/auth tokens;
- выгруженные секреты из HAR или конфигов.

Admin-креды телефонов хранятся только в памяти процесса через `SecureCredentialStorage` и живут ограниченное время сессии.

## Поддерживаемые семейства

### T30P / старый web servlet

Используется legacy-flow:

1. `GET /servlet?m=mod_listener&p=login&q=loginForm`
2. парсинг RSA `g_rsa_n` / `g_rsa_e`
3. `POST /servlet?m=mod_listener&p=login&q=login`
4. `GET /servlet?m=mod_data&p=status&q=load`

Этот путь уже работает хорошо для T30P.

### T4xx / T5xx / W70B / новый API

Используется HTTPS и modern API:

1. `POST /api/common/info?p=Login` с `idlist:["wui.common.rsaN","wui.common.rsaE"]`
2. RSA-шифрование пароля
3. `POST /api/auth/login?p=Login` с form field `username` и `pwd`
4. `POST /api/common/info?p=Login` с `idlist:["wui"]`
5. `POST /api/common/info?p=StatusGeneral` с нужными `idlist`

Для T4xx/T5xx/W70B важны HTTPS, browser-like headers (`Origin`, `Referer`, `User-Agent`) и правильное имя поля `username`.

## Известные модели и сборки

Модель нужно определять по версии сборки, а не только по версии ПО.

| Модель | Версия ПО | Сборка |
| --- | --- | --- |
| T43U | 108.86.14.8 | 107.1.0.0.3.1.0 |
| T46U | 108.86.14.8 | 108.1.0.0.3.1.0 |
| T57W | 96.86.14.3 | 97.1.2.0.2.1.2 |
| T30P | 124.86.14.5 | 127.0.0.0.2.0.0 |
| W70B | 146.85.14.4 | 146.0.0.0.2.0.0 |

## Важные заметки по сети

Корпоративная сеть может быть капризной:

- VPN может ломать доступ к подсети телефонов;
- у телефонов могут быть самоподписанные TLS-сертификаты;
- у части телефонов может быть `403 Forbidden`, если отключен Action URI;
- `403` не считается полной ошибкой сканирования: телефон нужно добавить в список и пометить как требующий настройки.

Если modern API падает с общей ошибкой:

```text
An error occurred while sending the request.
```

Нужно смотреть вложенную причину в UI/логах: SSL, routing/VPN, timeout, refused connection или проблема формата запроса.

## Документы

- [architecture.md](architecture.md) - текущая архитектура и потоки запросов.
- [TODO.md](TODO.md) - план работ и открытые вопросы.
