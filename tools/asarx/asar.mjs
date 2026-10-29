#!/usr/bin/env node
// Minimal asar reader (no npm deps). Read-only introspection of an .asar archive.
//   node asar.mjs <archive> ls [prefix]            list entries
//   node asar.mjs <archive> grep <regex> [prefix]  search text files, print hits
//   node asar.mjs <archive> cat <path>             dump one file to stdout
//   node asar.mjs <archive> x <path> <outFile>     extract one file
import fs from 'node:fs';
import path from 'node:path';

const [, , archive, cmd, a1, a2] = process.argv;
if (!archive || !cmd) {
  console.error('usage: asar.mjs <archive> ls|grep|cat|x ...');
  process.exit(2);
}

// Layout: [0..3]=payloadSize(4) [4..7]=headerSize [8..11]=headerPayload [12..15]=jsonLen | json @16
// file data begins at 8 + headerSize; entry.offset is relative to that.
const fd = fs.openSync(archive, 'r');
const pre16 = Buffer.alloc(16);
fs.readSync(fd, pre16, 0, 16, 0);
const headerSize = pre16.readUInt32LE(4);
const jsonLen = pre16.readUInt32LE(12);
const dataOffset = 8 + headerSize;

const jsonBuf = Buffer.alloc(jsonLen);
fs.readSync(fd, jsonBuf, 0, jsonLen, 16);
const header = JSON.parse(jsonBuf.toString('utf8'));

function walk(node, prefix, out) {
  for (const [name, val] of Object.entries(node.files || {})) {
    const p = prefix ? prefix + '/' + name : name;
    if (val.files) walk(val, p, out);
    else out.push({ path: p, size: val.size ?? 0, offset: val.offset, unpacked: !!val.unpacked, link: val.link });
  }
}
const entries = [];
walk(header, '', entries);
entries.sort((x, y) => x.path.localeCompare(y.path));

function readEntry(e) {
  if (e.unpacked) {
    return fs.readFileSync(path.join(archive + '.unpacked', e.path));
  }
  const buf = Buffer.alloc(e.size);
  fs.readSync(fd, buf, 0, e.size, dataOffset + Number(e.offset));
  return buf;
}

if (cmd === 'ls') {
  const pre = a1 || '';
  let total = 0;
  for (const e of entries) {
    if (pre && !e.path.startsWith(pre)) continue;
    total++;
    console.log(`${String(e.size).padStart(10)}  ${e.path}${e.unpacked ? '  [unpacked]' : ''}`);
  }
  console.error(`# ${total} shown / ${entries.length} total, dataOffset=${dataOffset}`);
} else if (cmd === 'grep') {
  const re = new RegExp(a1, 'i');
  const pre = a2 || '';
  const limit = 400;
  let hits = 0;
  for (const e of entries) {
    if (pre && !e.path.startsWith(pre)) continue;
    if (e.size > 40 * 1024 * 1024) continue;
    if (!/\.(js|mjs|cjs|json|ts|map|txt|html|vue)$/i.test(e.path)) continue;
    let text;
    try { text = readEntry(e).toString('utf8'); } catch { continue; }
    const lines = text.split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      if (re.test(lines[i])) {
        console.log(`${e.path}:${i + 1}: ${lines[i].slice(0, 400)}`);
        if (++hits >= limit) { console.error(`# truncated at ${limit}`); process.exit(0); }
      }
    }
  }
  console.error(`# ${hits} hits`);
} else if (cmd === 'cat') {
  const e = entries.find(x => x.path === a1);
  if (!e) { console.error('not found: ' + a1); process.exit(1); }
  process.stdout.write(readEntry(e));
} else if (cmd === 'x') {
  const e = entries.find(x => x.path === a1);
  if (!e) { console.error('not found: ' + a1); process.exit(1); }
  fs.mkdirSync(path.dirname(a2), { recursive: true });
  fs.writeFileSync(a2, readEntry(e));
  console.error(`# wrote ${a2} (${e.size} bytes)`);
} else {
  console.error('unknown cmd');
  process.exit(2);
}
