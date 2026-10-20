// 从 pinyin-pro 的 dict1.mjs 提取「汉字 -> 无调拼音」映射。
//
// 数据来源：pinyin-pro（https://github.com/zh-lx/pinyin-pro）
//   MIT License, Copyright (c) 2022-present zh-lx
//
// 用法（需要先 npm i pinyin-pro，或从 npm 镜像下 tarball 解开）：
//   node extract.mjs <path-to-pinyin-pro-package>
// 输出：pinyin-table.txt，每行「码位 拼音」，码位升序，同字多音保留第一个读音。
import fs from 'node:fs';
import path from 'node:path';

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const pkgDir = process.argv[2] || path.join(here, 'pinyin-pro');
const dictFile = path.join(pkgDir, 'dist/esm/data/dict1.mjs');

if (!fs.existsSync(dictFile)) {
  console.error(`找不到 ${dictFile}\n请先下载 pinyin-pro 并解开，把包目录作为参数传进来。`);
  process.exit(1);
}

// 声调字符 -> 无调元音（ü 统一记作 v，与原有数据一致）
const TONE_MAP = {
  'ā': 'a', 'á': 'a', 'ǎ': 'a', 'à': 'a',
  'ē': 'e', 'é': 'e', 'ě': 'e', 'è': 'e',
  'ī': 'i', 'í': 'i', 'ǐ': 'i', 'ì': 'i',
  'ō': 'o', 'ó': 'o', 'ǒ': 'o', 'ò': 'o',
  'ū': 'u', 'ú': 'u', 'ǔ': 'u', 'ù': 'u',
  'ǖ': 'v', 'ǘ': 'v', 'ǚ': 'v', 'ǜ': 'v',
  'ü': 'v', 'ń': 'n', 'ň': 'n', 'ǹ': 'n', 'ḿ': 'm',
};

function stripTone(s) {
  let out = '';
  for (const ch of s) out += TONE_MAP[ch] ?? ch;
  return out;
}

const text = fs.readFileSync(dictFile, 'utf8');

// 条目形如：  'bǎng páng pāng': ['膀'],  或  líng: ['〇', '伶', ...]
const entryRe = /^\s*(?:'([^']*)'|([A-Za-z\u00c0-\u024f\u0100-\u01ff]+))\s*:\s*\[([^\]]*)\]/gm;

const table = new Map(); // char -> pinyin（同字多音时保留第一个）
let entries = 0;
let m;
while ((m = entryRe.exec(text)) !== null) {
  const keyRaw = m[1] !== undefined ? m[1] : m[2];
  const firstTone = keyRaw.trim().split(/\s+/)[0];
  if (!firstTone) continue;

  const py = stripTone(firstTone);
  if (!/^[a-z]+$/.test(py)) continue;
  if (py.length < 1 || py.length > 7) continue;

  for (const hit of m[3].matchAll(/'([^']*)'/g)) {
    const ch = hit[1];
    const cp = ch.codePointAt(0);
    // 只收 BMP 内的汉字：C# 侧用 char（UTF-16 单码元）查表
    if (cp < 0x10000 && !table.has(ch)) table.set(ch, py);
  }
  entries++;
}

const rows = [...table.entries()]
  .map(([ch, py]) => [ch.codePointAt(0), py])
  .sort((a, b) => a[0] - b[0]);

fs.writeFileSync(path.join(here, 'pinyin-table.txt'), rows.map(([c, p]) => `${c} ${p}`).join('\n') + '\n');

console.log(`条目=${entries}  汉字=${rows.length}  音节=${new Set(rows.map((x) => x[1])).size}`);
console.log(`输出 ${path.join(here, 'pinyin-table.txt')}`);
