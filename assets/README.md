# Ресурсы

В программу входит всё содержимое этой папки, кроме `src/`.

| Путь | Что это |
| --- | --- |
| `palette.json` | Единая палитра. Символ `key` обозначает цвет в текстовых спрайтах. |
| `src/sprites/*.txt` | Исходники спрайтов. Редактируются здесь. |
| `sprites/atlas.png`, `sprites/atlas.json` | Собранный атлас. Руками не меняется. |
| `app.ico` | Значок программы, собирается из спрайта `app_icon`. |
| `fonts/*.json` | Растровые шрифты: `body` для текста, `title` для заголовков. |
| `fonts/licenses/` | Лицензии шрифтов. |
| `sounds/sniper-shot.wav` | 8-битный выстрел снайпера. |
| `sounds/random/*.wav` | Свои звуки «Случайных звуков»: стук в дверь, шаги, капающая вода. Новый звук добавляется файлом в эту папку. |
| `sounds/ui/*.wav` | 8-битные звуки событий игры: покупка, задание, уровень, сундук и другие. Какой звук на что — `data/sound/cues.json`. |
| `sounds/music/theme.wav` | Фоновая мелодия по кругу, около 15 секунд. Выключена по умолчанию. |

## Спрайты

Формат описан в `src/ADHDWindows.Core/Pixels/SpriteSource.cs`. Сетка: 16×16 для иконок, 32×32 для крупных объектов. Параметр `slice` задаёт неподвижные края рамки, `fps` — скорость кадров.

После правки исходников соберите атлас из корня репозитория:

```bash
dotnet run --project tools/ADHDWindows.AssetTool -- sprites assets
```

Тест `AssetTests` не пройдёт, если атлас не соответствует исходникам.

## Шрифты

Оба шрифта распространяются по SIL Open Font License 1.1 и переведены в растр с сетки контуров. Исходные TTF в репозиторий не входят.

| Файл | Источник | Команда |
| --- | --- | --- |
| `fonts/body.json` | Galmuri9, <https://github.com/quiple/galmuri> | `font Galmuri9.ttf 10 body assets/fonts/body.json` |
| `fonts/title.json` | Press Start 2P, <https://github.com/google/fonts/tree/main/ofl/pressstart2p> | `font PressStart2P-Regular.ttf 8 title assets/fonts/title.json` |

Команды выполняются через `dotnet run --project tools/ADHDWindows.AssetTool -- <команда>`. Название Press Start 2P зарезервировано лицензией, поэтому изменённая версия называется `title`.

## Звуки

Звуки 8 бит, моно, 22 050 Гц. Свои звуки синтезирует инструмент ресурсов с постоянным зерном случайности, поэтому повторный запуск даёт те же файлы:

```bash
dotnet run --project tools/ADHDWindows.AssetTool -- sounds assets
```

Звуки Windows для «Случайных звуков» берутся из `C:\Windows\Media` компьютера пользователя, их список — в `data/hardships/sounds.json`.
