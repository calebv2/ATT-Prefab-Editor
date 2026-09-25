#!/usr/bin/env node
// Pulls the item images out of Hazy's Item List (community Google Sheet,
// "Items With Pictures" tab) into data/item-images/<hash>.<ext> + index.json.
//
//   node data/fetch-item-images.js
//
// The published sheet renders its grid server-side at pubhtml/sheet — each row
// is <td>hash</td><td><img></td><td>name</td>, so the game's prefab HASH is
// the join key straight into our vendored catalog (no name matching).
// Images are community screenshots; the folder is gitignored — rerun this
// script to (re)populate. Credit: Hazy's Item List.

'use strict';

const fs = require('fs');
const path = require('path');

const GRID_URL = 'https://docs.google.com/spreadsheets/d/e/2PACX-1vR7sXiFWQc4zX_gmBYF3CHJTaNfWNlB5EP7KOkl5DrzGxVqsxaXBi28w-VZ0wn6kLk6R3j6c3miKNsg/pubhtml/sheet?headers=false&gid=1678646626';
const OUT_DIR = path.join(__dirname, 'item-images');
const INDEX = path.join(OUT_DIR, 'index.json');
const CONCURRENCY = 8;

function decodeEntities(s) {
    return String(s || '')
        .replace(/&#(\d+);/g, (_, n) => String.fromCharCode(parseInt(n, 10)))
        .replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
        .replace(/&quot;/g, '"').replace(/&#39;/g, "'");
}

function extOf(buf) {
    if (buf.length > 8 && buf[0] === 0x89 && buf[1] === 0x50) { return 'png'; }
    if (buf.length > 3 && buf[0] === 0xff && buf[1] === 0xd8) { return 'jpg'; }
    if (buf.length > 12 && buf.toString('ascii', 8, 12) === 'WEBP') { return 'webp'; }
    if (buf.length > 6 && buf.toString('ascii', 0, 3) === 'GIF') { return 'gif'; }
    return 'png';
}

async function fetchBuf(url) {
    const res = await fetch(url, { headers: { 'User-Agent': 'Mozilla/5.0' } });
    if (!res.ok) { throw new Error('HTTP ' + res.status); }
    return Buffer.from(await res.arrayBuffer());
}

async function main() {
    console.log('[images] fetching the published grid...');
    const html = (await fetchBuf(GRID_URL)).toString('utf8');

    // rows: hash td, optional image td, name td
    const rows = [];
    for (const tr of html.split(/<tr[ >]/).slice(1)) {
        const tds = [...tr.matchAll(/<td[^>]*>([\s\S]*?)<\/td>/g)].map((m) => m[1]);
        if (tds.length < 3) { continue; }
        const hash = /^\d+$/.test(tds[0].trim()) ? parseInt(tds[0].trim(), 10) : null;
        if (!hash) { continue; }
        const img = (tds[1].match(/<img src="([^"]+)"/) || [])[1] || null;
        const name = decodeEntities(tds[2].replace(/<[^>]+>/g, '').trim());
        if (!name) { continue; }
        rows.push({ hash, img, name });
    }
    const withImg = rows.filter((r) => r.img);
    console.log('[images] parsed ' + rows.length + ' item rows, ' + withImg.length + ' with an image.');

    fs.mkdirSync(OUT_DIR, { recursive: true });
    const index = { generated: new Date().toISOString(), source: "Hazy's Item List (community sheet)", items: {} };
    for (const r of rows) { if (!r.img) { index.items[r.hash] = { name: r.name, file: null }; } }

    let done = 0, failed = 0;
    const queue = [...withImg];
    async function worker() {
        for (;;) {
            const r = queue.shift();
            if (!r) { return; }
            try {
                // the grid serves =w150-h149 thumbs; ask for =s512 first, fall back
                let buf = null;
                const big = r.img.replace(/=w\d+-h\d+$/, '=s512');
                if (big !== r.img) { try { buf = await fetchBuf(big); } catch { /* fall back */ } }
                if (!buf) { buf = await fetchBuf(r.img); }
                const file = r.hash + '.' + extOf(buf);
                fs.writeFileSync(path.join(OUT_DIR, file), buf);
                index.items[r.hash] = { name: r.name, file };
            } catch (e) {
                failed++;
                index.items[r.hash] = { name: r.name, file: null };
                console.log('[images] FAILED ' + r.hash + ' ' + r.name + ': ' + e.message);
            }
            done++;
            if (done % 100 === 0) { console.log('[images] ' + done + '/' + withImg.length + '...'); }
        }
    }
    await Promise.all(Array.from({ length: CONCURRENCY }, worker));

    fs.writeFileSync(INDEX, JSON.stringify(index, null, 1));
    console.log('[images] done: ' + (done - failed) + ' saved, ' + failed + ' failed, index -> ' + INDEX);
}

main().catch((e) => { console.error('[images] fatal: ' + e.message); process.exit(1); });
