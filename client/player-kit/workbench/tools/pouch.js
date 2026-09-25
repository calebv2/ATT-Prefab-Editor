#!/usr/bin/env node
// pouch.js — build "a pouch holding N of <item>" save strings.
//
//   node tools/pouch.js <Prefab> <count> [dockedTypeHash]
//   node tools/pouch.js Flint 30
//   node tools/pouch.js --decode "<save string>"
//
// The encoder itself lives in pouch-core.js, shared with the workbench's
// POST /api/pouch so the CLI and the UI can never drift apart. This file is
// argument parsing and printing, nothing else.
//
// The one value we cannot derive offline is `dockedTypeHash` — it is a runtime
// type hash, not the prefab hash (Flint's prefab is 39484 but its docked type
// is 42570), and it appears in no catalog we vendor. To learn it for a new
// item: dock that item into a pouch in-game, capture the pouch string with the
// editor's `Copy string`, and run `node tools/pouch.js --decode "<string>"`.
// data/dockedTypes.json is that lookup table; add to it as they are captured.

'use strict';

const core = require('./pouch-core');

function decode(str) {
    const info = core.inspect(str);
    console.log('prefab        : ' + info.name + ' (' + info.hash + ')');
    console.log('entities      : ' + info.entities.join(', '));
    if (info.dock) {
        console.log('PickupDock    : dockedTypeHash=' + info.dock.dockedTypeHash
            + '  quantity=' + info.dock.quantity + '  childIndex=' + info.dock.childIndex);
    } else {
        console.log('PickupDock    : (none — this pouch is empty)');
    }
    for (const c of info.children) {
        console.log('child         : ' + c.name + ' (' + c.hash + ') @parentHash ' + c.parentHash);
        if (c.newMapping !== null) {
            console.log('  >>> NEW MAPPING: add  "' + c.name + '": ' + c.newMapping
                + ',  to data/dockedTypes.json');
        }
    }
    return info.prefab;
}

function fail(e) {
    if (!(e instanceof core.PouchError)) { throw e; }
    console.error(e.message);
    process.exit(1);
}

const argv = process.argv.slice(2);
if (argv[0] === '--decode') {
    if (!argv[1]) { console.error('usage: node tools/pouch.js --decode "<save string>"'); process.exit(1); }
    decode(argv[1]);
} else if (argv.length >= 2) {
    const [item, countRaw, typeHash] = argv;
    let p, count;
    try {
        count = core.parseQuantity(countRaw);
        p = core.buildPouch(item, count, typeHash);
    } catch (e) { fail(e); }
    const out = p.toSaveString();
    console.log('# pouch of ' + item + ' x' + count);
    console.log(out);
    const note = core.quantityNote(count);
    if (note) { console.log('# note: ' + note); }
    console.log('\n--- verify ---');
    decode(out);
} else {
    console.log('usage:');
    console.log('  node tools/pouch.js <Prefab> <count> [dockedTypeHash]   build');
    console.log('  node tools/pouch.js --decode "<save string>"            inspect / learn a type hash');
    console.log('\nknown types: ' + JSON.stringify(core.knownTypes()));
}
