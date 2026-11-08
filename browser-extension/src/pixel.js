// Пиксельные шрифты и спрайты ADHDWindows: те же файлы, что у окон программы.

/** Файлы данных программы допускают строки-комментарии, как JSON в C#. */
export async function loadJson(path) {
  const response = await fetch(path);
  const text = await response.text();
  return JSON.parse(text.replace(/^\s*\/\/.*$/gm, ""));
}

export async function loadFont(path) {
  const font = await loadJson(path);
  font.map = new Map(font.glyphs.map((glyph) => [glyph.code, glyph]));
  font.fallback = font.map.get("?".codePointAt(0));
  return font;
}

export async function loadAtlas() {
  const [atlas, image] = await Promise.all([
    loadJson("pixel/atlas.json"),
    new Promise((resolve, reject) => {
      const picture = new Image();
      picture.onload = () => resolve(picture);
      picture.onerror = reject;
      picture.src = "pixel/atlas.png";
    }),
  ]);
  atlas.image = image;
  atlas.map = new Map(atlas.sprites.map((sprite) => [sprite.name, sprite]));
  return atlas;
}

export async function loadPalette() {
  const palette = await loadJson("pixel/palette.json");
  return Object.fromEntries(palette.colors.map((color) => [color.name, color.hex]));
}

function glyph(font, symbol) {
  return font.map.get(symbol.codePointAt(0)) ?? font.fallback;
}

export function measure(font, text) {
  let width = 0;
  for (const symbol of text) {
    width += glyph(font, symbol)?.advance ?? 0;
  }

  return width;
}

/** Разбивает текст на строки не шире width пикселей. */
export function wrap(font, text, width) {
  const lines = [];
  for (const paragraph of text.split("\n")) {
    let line = "";
    for (const word of paragraph.split(" ")) {
      const candidate = line ? line + " " + word : word;
      if (line && measure(font, candidate) > width) {
        lines.push(line);
        line = word;
      } else {
        line = candidate;
      }
    }

    lines.push(line);
  }

  return lines;
}

/** Рисует текст в пикселях шрифта, (x, y) — левый верхний угол строки. */
export function drawText(context, font, text, x, y, color) {
  context.fillStyle = color;
  let cursor = x;
  for (const symbol of text) {
    const item = glyph(font, symbol);
    if (!item) {
      continue;
    }

    item.rows.forEach((row, rowIndex) => {
      for (let column = 0; column < row.length; column++) {
        if (row[column] === "#") {
          context.fillRect(cursor + item.offsetX + column, y + item.offsetY + rowIndex, 1, 1);
        }
      }
    });
    cursor += item.advance;
  }
}

export function drawSprite(context, atlas, name, x, y, frame = 0) {
  const sprite = atlas.map.get(name);
  if (!sprite) {
    return;
  }

  const column = frame % Math.max(1, sprite.frames);
  context.drawImage(atlas.image, sprite.x + column * sprite.width, sprite.y, sprite.width, sprite.height, x, y, sprite.width, sprite.height);
}

/** Рамка панели как у окон программы: тёмный контур, светлая кромка сверху. */
export function drawPanel(context, colors, x, y, width, height, fill = colors.panel) {
  context.fillStyle = colors.ink;
  context.fillRect(x, y, width, height);
  context.fillStyle = colors.line;
  context.fillRect(x + 1, y + 1, width - 2, height - 2);
  context.fillStyle = fill;
  context.fillRect(x + 2, y + 2, width - 4, height - 4);
}
