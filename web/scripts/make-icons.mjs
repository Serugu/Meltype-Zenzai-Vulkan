// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Konayukiw

// アプリとサイトのアイコン (雪の結晶 + M) を public/favicon.svg から作る。
//   npm i --no-save playwright-core
//   node scripts/make-icons.mjs            (Chrome / Edge / Chromium の場所は CHROMIUM_PATH で指定できる)
// 作るもの:
//   web/public/apple-touch-icon.png  180x180、明るい背景で余白を付ける (角は iOS が丸める)
//   native/tip/meltype.ico           16〜256 の 9 サイズ (Meltype IME と Meltype.exe)
//   linux/icon.png                   64x64
//   mac/Resources/icon.tiff          32x32 の白黒テンプレート (メニューバー用。macOS がダークモードで白く出す)

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "..", "..");
const { chromium } = await import("playwright-core");

const candidates = [
  process.env.CHROMIUM_PATH,
  "C:/Program Files/Google/Chrome/Application/chrome.exe",
  "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
  "/usr/bin/chromium",
  "/usr/bin/google-chrome",
].filter(Boolean);
const executablePath = candidates.find((path) => existsSync(path));

const svg = readFileSync(join(here, "..", "public", "favicon.svg"), "utf8");
const inner = svg.replace(/^[\s\S]*?<svg[^>]*>/, "").replace(/<\/svg>\s*$/, "");

// メニューバー用: 結晶は薄い黒、M は濃い黒。M のまわりの白い縁は透明にして結晶と切り離す
const flake = inner.match(/<path d="(M30 42V22[^"]*)"/)[1];
const m = "M21.5 50V34l8.5 10 8.5-10v16";
const template = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="5 17 50 50">
<defs><mask id="k" maskUnits="userSpaceOnUse" x="0" y="0" width="60" height="80">
<rect x="0" y="0" width="60" height="80" fill="#fff"/>
<path d="${m}" fill="none" stroke="#000" stroke-width="10" stroke-linecap="round" stroke-linejoin="round"/></mask></defs>
<g fill="none" stroke-linecap="round" stroke-linejoin="round">
<path d="${flake}" stroke="#000" stroke-opacity="0.55" stroke-width="4" mask="url(#k)"/>
<path d="${m}" stroke="#000" stroke-width="6"/></g></svg>`;

const browser = await chromium.launch(executablePath ? { executablePath } : {});
const page = await browser.newPage({ deviceScaleFactor: 1 });

async function render(size, { pad = 0, background = null, source = svg } = {}) {
  const body = source.replace("<svg ", `<svg width="${size - 2 * pad}" height="${size - 2 * pad}" `);
  await page.setViewportSize({ width: size, height: size });
  await page.setContent(
    `<html><body style="margin:0;background:${background ?? "transparent"}">` +
      `<div style="padding:${pad}px;width:${size - 2 * pad}px;height:${size - 2 * pad}px;line-height:0">${body}</div></body></html>`,
  );
  return page.screenshot({ omitBackground: !background, clip: { x: 0, y: 0, width: size, height: size } });
}

// PNG の画素 (RGBA) を取り出す
async function pixels(png) {
  return page.evaluate(async (b64) => {
    const img = new Image();
    img.src = `data:image/png;base64,${b64}`;
    await img.decode();
    const c = document.createElement("canvas");
    c.width = img.width;
    c.height = img.height;
    const g = c.getContext("2d");
    g.drawImage(img, 0, 0);
    return Array.from(g.getImageData(0, 0, img.width, img.height).data);
  }, png.toString("base64"));
}

// 中身が PNG の ICO
function ico(entries) {
  const header = Buffer.alloc(6 + 16 * entries.length);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(entries.length, 4);
  let offset = header.length;
  entries.forEach(({ size, png }, i) => {
    const o = 6 + 16 * i;
    header.writeUInt8(size >= 256 ? 0 : size, o);
    header.writeUInt8(size >= 256 ? 0 : size, o + 1);
    header.writeUInt16LE(1, o + 4);
    header.writeUInt16LE(32, o + 6);
    header.writeUInt32LE(png.length, o + 8);
    header.writeUInt32LE(offset, o + 12);
    offset += png.length;
  });
  return Buffer.concat([header, ...entries.map((e) => e.png)]);
}

// 圧縮なし RGBA (アルファは乗算なし) の TIFF
function tiff(width, height, rgba) {
  const data = Buffer.from(rgba);
  const tags = [
    [256, 3, 1, width], [257, 3, 1, height], [258, 3, 4, 0], [259, 3, 1, 1], [262, 3, 1, 2],
    [273, 4, 1, 0], [277, 3, 1, 4], [278, 3, 1, height], [279, 4, 1, data.length], [284, 3, 1, 1], [338, 3, 1, 2],
  ];
  const ifdSize = 2 + tags.length * 12 + 4;
  const bitsOffset = 8 + ifdSize;
  const dataOffset = bitsOffset + 8;
  const head = Buffer.alloc(dataOffset);
  head.write("II", 0, "latin1");
  head.writeUInt16LE(42, 2);
  head.writeUInt32LE(8, 4);
  head.writeUInt16LE(tags.length, 8);
  tags.forEach(([tag, type, count, value], i) => {
    const o = 10 + i * 12;
    if (tag === 258) value = bitsOffset;
    if (tag === 273) value = dataOffset;
    head.writeUInt16LE(tag, o);
    head.writeUInt16LE(type, o + 2);
    head.writeUInt32LE(count, o + 4);
    if (type === 3 && count === 1) head.writeUInt16LE(value, o + 8);
    else head.writeUInt32LE(value, o + 8);
  });
  for (let i = 0; i < 4; i++) head.writeUInt16LE(8, bitsOffset + i * 2);
  return Buffer.concat([head, data]);
}

function save(path, bytes) {
  writeFileSync(join(repo, path), bytes);
  console.log(`wrote ${path}`);
}

save("web/public/apple-touch-icon.png", await render(180, { pad: 14, background: "#f5f9ff" }));
save("linux/icon.png", await render(64));
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
const entries = [];
for (const size of sizes) entries.push({ size, png: await render(size) });
save("native/tip/meltype.ico", ico(entries));
save("mac/Resources/icon.tiff", tiff(32, 32, await pixels(await render(32, { source: template }))));

await browser.close();
