# 9 багов — Дизайн исправлений

Дата: 2025-06-25
Статус: Утверждён

## Баги 1-2: Welcome screen + кнопка «Выбрать папку»

**Корень:** `WelcomeOverlay` — непрозрачный Grid поверх всего UI (MainWindow.xaml:516-530), блокирует кнопку. `IsEmpty = true` в `ScanAndAnalyze` (MainViewModel.cs:282) держит overlay видимым во время сканирования.

**Фикс:**
- Убрать `WelcomeOverlay` как перекрывающий слой
- Приветствие показывать вместо основного контента через Visibility-переключение главного контейнера
- `ShowWelcome = false` сразу при старте сканирования
- Кнопка «Выбрать папку» всегда кликабельна

## Баги 3-4: Множественный выбор + дозапись

**Корень:** `Window_Drop` берёт только `files[0]` (MainWindow.xaml.cs:89). `Files.Clear()` безусловно (MainViewModel.cs:288).

**Фикс:**
- Кнопка «📁 Добавить» — `OpenFileDialog(Multiselect=true)`, фильтр аудио
- Drag-drop принимает список: файлы → напрямую, папки → рекурсивно
- `ScanAndAppend`: пропускать дубликаты по `FilePath`, анализировать только новые
- `PopulateArtistGroups` перестраивается полностью (дешёво)

## Баг 5: Обложка в карточке трека

**Корень:** Binding к `SelectedGroup.CoverData` (MainWindow.xaml:342), но `OnSelectionChanged` не выставляет `SelectedGroup`.

**Фикс:**
- Сменить binding обложки на `SelectedFile.CoverData`
- Плэйсхолдер 🎵 когда `CoverData == null`

## Баг 6: Спектрограмма

**Корень:** Битмап 300×256 (SpectrogramBuilder:10), шрифт — залитые прямоугольники без глифов (SpectrogramRenderer:116-133).

**Фикс:**
- `SpectrogramBuilder`: `FreqBins` 256→512, `MaxFrames` 300→600
- `SpectrogramRenderer`: вернуть только битмап без осей
- Оси/сетка/cutoff — XAML Canvas поверх Image (TextBlock + Line, настоящий шрифт Segoe UI 9pt)
- Убрать `DrawText`, `DrawHLine`, `DrawVLine` из `SpectrogramRenderer`

## Баг 7: Оценка альбома

**Корень:** `AlbumGroup` не имеет агрегированных полей (GroupModels.cs:11-17). `PopulateArtistGroups` не агрегирует (MainViewModel.cs:362-392).

**Фикс:**
- `AlbumGroup` новые поля: `AverageLosslessScore`, `AverageQualityScore`, `AverageDynamicRange`, `AlbumVerdict`, `KeepCount/InvestigateCount/ReplaceCount`, `TotalTracks`
- Логика: средние по применимым метрикам
- Консенсус: все KEEP → LOSSLESS, есть INVESTIGATE → NOT SURE, любой REPLACE → REPLACE
- Цветной бейдж в TreeView (🟢/🟡/🔴 + текст вердикта)
- Сводка в детальной панели при выборе альбома

## Баг 8: Подсветка выделения

**Корень:** DataGrid без кастомных selection-кистей (MainWindow.xaml:227-306), в отличие от TreeView.

**Фикс:**
- `DataGrid.Resources`: `SystemColors.HighlightBrushKey`, `HighlightTextBrushKey`, `InactiveSelectionHighlightBrushKey`, `InactiveSelectionHighlightTextBrushKey`
- `DataGridCell` стиль: `IsSelected` триггер для Foreground

## Баг 9: Вердикты + детализация

**Корень:** Разнобой меток в разных местах (MainWindow.xaml:218-224 фильтры, :431-446 verdict bar, AudioFileViewModel.cs:501-517 метрики).

**Фикс:**
- Единая схема: `LOSSLESS` (зелёный), `NOT SURE` (жёлтый), `REPLACE` (красный)
- Спецметки: `HI-RES` (зелёный), `MP3 320`/`MP3 192`/`MP3 128` (красный)
- `VerdictLabel` computed property в VM — используется везде (DataGrid, verdict bar, фильтры)
- Блок «Почему?» под вердиктом: 2-3 предложения с ключевыми метриками
- Генерация в `BuildMetricItems` или `VerdictGenerator`
