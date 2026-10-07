# Black Myth: Wukong Benchmark Automation (C#)

Консольный инструмент на C#, который автоматизирует два последовательных прогона **Black Myth: Wukong Benchmark Tool**:

1. **CPU-bound pass** - 1280x720, низкие настройки, низкий render scale, RT и frame generation отключены. Цель - максимально уменьшить стоимость кадра на GPU и сильнее проявить CPU-limit.
2. **GPU-bound pass** - нативное разрешение рабочего стола, Cinematic-настройки, 100% render scale, RT включается на GPU, который эвристически определяется как hardware-RT-capable. Frame generation отключён, чтобы FPS отражал отрендеренные кадры, а не сгенерированные промежуточные кадры.

Инструмент также собирает CPU/GPU/RAM/OS, делает backup конфигов, применяет профиль, запускает Benchmark Tool, пытается активировать основной пункт меню клавишей Enter, ждёт результат, ищет метрики в новых/изменённых файлах `b1\Saved`, формирует `.txt` и `.json` отчёты и восстанавливает исходные конфиги.

## Требования

- Windows 10/11
- .NET 8 SDK
- установленный бесплатный **Black Myth: Wukong Benchmark Tool** в Steam

## Запуск

```powershell
dotnet run --project WukongBenchmarkAutomation.csproj
```

Если Steam/игра установлены нестандартно и автоопределение не сработало:

```powershell
dotnet run --project WukongBenchmarkAutomation.csproj -- --benchmark-dir="D:\SteamLibrary\steamapps\common\Black Myth Wukong Benchmark Tool"
```

После двух проходов рядом с текущим рабочим каталогом появятся:

- `wukong-benchmark-YYYYMMDD-HHMMSS.txt`
- `wukong-benchmark-YYYYMMDD-HHMMSS.json`

## Что автоматизировано

- поиск Steam library и Benchmark Tool;
- сбор характеристик ПК;
- backup `GameUserSettings.ini` / `Engine.ini`;
- применение CPU/GPU профиля;
- запуск `b1_benchmark.exe`;
- попытка запуска benchmark через клавиатурное управление главным окном;
- ожидание завершения/появления результатов;
- поиск и разбор FPS/VRAM из свежих `.log/.txt/.json/.csv/.ini` в `b1\Saved`;
- формирование отчёта;
- восстановление пользовательских конфигов.

## Выбор настроек

### CPU test

- 1280x720;
- Low (`sg.* = 0`);
- ResolutionQuality = 50%;
- Ray Tracing = Off;
- Frame Generation = Off;
- VSync/FPS cap = Off.

Это снижает GPU frame time. В результате при достаточно производительной видеокарте ограничивающим компонентом раньше становится CPU.

### GPU test

- нативное разрешение рабочего стола, минимум 1920x1080;
- Cinematic (`sg.* = 4`);
- ResolutionQuality = 100%;
- Ray Tracing = On на RTX / Intel Arc / Radeon RX 6000+;
- Frame Generation = Off;
- VSync/FPS cap = Off.

Это увеличивает стоимость рендеринга кадра и переносит bottleneck на GPU. Frame Generation отключён специально, чтобы не смешивать реальные и синтезированные кадры в метрике FPS.

## Ограничения

Benchmark Tool не публикует стабильный внешний automation API. Поэтому проект не привязан к координатам мыши: основной пункт меню активируется через Win32 keyboard message, а результаты ищутся в machine-readable файлах, которые Benchmark Tool создаёт/обновляет в `b1\Saved`.

Если конкретная версия Benchmark Tool не пишет FPS в текстовые файлы, инструмент не подменяет результат выдуманным значением: в отчёте будет `metrics were not found automatically`. Такой fail-safe выбран вместо OCR/жёстко заданных координат, которые зависят от языка интерфейса, DPI и разрешения.

## Структура

- `Program.cs` - вся логика автоматизации
- `WukongBenchmarkAutomation.csproj` - проект .NET 8
