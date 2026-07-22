# TsqlFormatter

Форматтер T-SQL: приводит выделенный SQL к единому стилю прямо в SSMS.
Полный список правил форматирования — в [RULES.md](RULES.md).

## Быстрая настройка в SSMS

### Режим `--auto` — один хоткей (рекомендуется)

Выделил SQL → нажал хоткей → на месте появился отформатированный код.
Форматтер сам копирует выделение, форматирует и вставляет обратно — без ручного
Ctrl+C / Ctrl+V, без окон и сообщений.

1. Скопируй `TsqlFormatter.exe` (вместе с `TsqlFormatter.dll`,
   `TsqlFormatter.runtimeconfig.json`, `TsqlFormatter.deps.json` из
   `TsqlFormatter/bin/Release/net5.0/`) в удобную папку, например `C:\Tools\TsqlFormatter\`.
2. **Tools ▸ External Tools ▸ Add:**
   - **Title:** `Format SQL`
   - **Command:** `C:\Tools\TsqlFormatter\TsqlFormatter.exe`
   - **Arguments:** `--auto`
   - ❌ снять галку **Use Output window**
   - ❌ снять галку **Prompt for arguments**
   - OK.
3. Запомни номер `N` этого пункта в списке External Tools (сверху вниз, начиная с 1).
4. **Tools ▸ Options ▸ Environment ▸ Keyboard:**
   - в поле «Show commands containing» введи `Tools.ExternalCommand`;
   - выбери `Tools.ExternalCommand{N}`;
   - поставь курсор в «Press shortcut keys», нажми желаемое сочетание (например `Ctrl+Shift+F`);
   - **Assign** → OK.

Готово: выдели фрагмент SQL и нажми хоткей.

> Примечание: после форматирования в буфере обмена остаётся отформатированный SQL
> (прежнее содержимое буфера заменяется).

### Режим `--clipboard` — запасной вариант

Если автоматические Ctrl+C/Ctrl+V по каким-то причинам не срабатывают, используйте
режим через буфер обмена (тоже без окон и сообщений):

- **Arguments:** `--clipboard`
- Работа: выдели SQL → **Ctrl+C** → хоткей → **Ctrl+V**.

## Требования

- Установленный **.NET 5 Runtime** (`winget install Microsoft.DotNet.Runtime.5`).

## Сборка из исходников

```
dotnet build -c Release
```

Готовый `TsqlFormatter.exe` появится в `TsqlFormatter/bin/Release/net5.0/`.

## Прочие режимы запуска

| Аргументы        | Что делает                                             |
|------------------|--------------------------------------------------------|
| `--auto`         | форматирует выделение в редакторе на месте (для SSMS)  |
| `--clipboard`    | форматирует текст из буфера обмена на месте            |
| `--stdin`        | читает stdin, пишет результат в stdout                 |
| `<путь\file.sql>`| форматирует файл на месте                              |

## Тесты

```
run-tests.bat                 — все тесты
run-tests.bat --rule 2.6      — только правило 2.6
run-tests.bat -v              — печатать вывод каждого теста
```

Подробнее о правилах и написании тестов — в [RULES.md](RULES.md).
