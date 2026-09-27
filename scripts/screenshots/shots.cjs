#!/usr/bin/env node
// Screenshot history CLI. No dependencies — `node scripts/screenshots/shots.cjs <command>`.
//
//   index                                   rebuild INDEX.md + index.json from runs/ (idempotent)
//   compare [--from <runId>] [--to <runId>] changed / new / missing / same between two runs
//                                           (defaults: the run before the latest → the latest)
//   history <text>                          every run's image for cases whose key contains <text>
//   promote <runId> [--force]               copy that run's wiki shots into docs/help/assets/screenshots
//   import --label <l> --dir <d> [--doc] [--sha <sha>] [--branch <b>] [--when <iso>] [--source-note <text>]
//                                           archive an existing folder of PNGs as a run (nothing is moved)
//
// Nothing here deletes a run. promote overwrites the wiki copy on purpose, records who/what/when in
// promotions.json, and the replaced image stays in git history and (once imported) in the archive.
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const lib = require("./lib.cjs");

function arg(args, name, fallback = undefined) {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
}

function die(message) {
  console.error(message);
  process.exit(1);
}

function runsOrDie() {
  const runs = lib.loadRuns();
  if (!runs.length) die(`No runs archived yet under ${lib.archiveRoot()}.`);
  return runs;
}

// Accepts a full run id, a prefix, or any unique fragment (e.g. a label or a commit sha). An ambiguous
// fragment is refused with the candidates rather than silently picking one.
function findRun(runs, id) {
  const exact = runs.find((r) => r.runId === id);
  if (exact) return exact;
  const hits = runs.filter((r) => r.runId.includes(id));
  if (hits.length === 1) return hits[0];
  const list = (hits.length ? hits : runs).map((r) => r.runId).join("\n  ");
  die(hits.length ? `'${id}' matches ${hits.length} runs — be more specific:\n  ${list}` : `No run matches '${id}'. Known runs:\n  ${list}`);
}

function cmdIndex() {
  const index = lib.buildIndex();
  console.log(`Indexed ${index.runs.length} run(s), ${index.cases.length} case(s) → ${path.join(lib.archiveRoot(), "INDEX.md")}`);
}

function cmdCompare(args) {
  const runs = runsOrDie();
  const to = findRun(runs, arg(args, "--to", runs[runs.length - 1].runId));
  const toPos = runs.indexOf(to);
  const fromId = arg(args, "--from", toPos > 0 ? runs[toPos - 1].runId : null);
  if (!fromId) die(`Only one run (${to.runId}) — nothing to compare it with yet.`);
  const from = findRun(runs, fromId);

  const byKey = (run) => new Map((run.shots || []).map((s) => [lib.caseKey(s), s]));
  const a = byKey(from);
  const b = byKey(to);
  const root = lib.archiveRoot();
  const abs = (s) => path.join(root, s.file);
  const rows = { changed: [], minor: [], new: [], missing: [], same: [] };
  for (const [key, shot] of b) {
    const prev = a.get(key);
    if (!prev) {
      rows.new.push({ key, to: shot });
      continue;
    }
    // Compare what the images LOOK like, never their bytes (see lib.decodePng for why).
    const hashA = prev.pixelHash || lib.pixelHash(abs(prev));
    const hashB = shot.pixelHash || lib.pixelHash(abs(shot));
    if (hashA === hashB) {
      rows.same.push({ key, from: prev, to: shot, diff: { ratio: 0 } });
      continue;
    }
    const diff = lib.pixelDiff(abs(prev), abs(shot));
    rows[lib.classify(diff.ratio)].push({ key, from: prev, to: shot, diff });
  }
  for (const [key, shot] of a) if (!b.has(key)) rows.missing.push({ key, from: shot });

  const wantDiff = args.includes("--diff");
  const diffDir = path.join(root, "diffs", `${from.runId}__${to.runId}`);
  const pct = (d) => (d.diff.resized ? "resized" : `${(d.diff.ratio * 100).toFixed(2)}% of pixels moved`);
  const dims = (s) => `${s.width}×${s.height}`;

  console.log(`Compare  ${from.runId}  →  ${to.runId}`);
  console.log(`         ${from.git?.sha || "?"} (${from.source})  →  ${to.git?.sha || "?"} (${to.source})\n`);
  console.log(
    `changed ${rows.changed.length} · minor ${rows.minor.length} · new ${rows.new.length} · ` +
      `missing ${rows.missing.length} · same ${rows.same.length}` +
      `   (minor = ≤0.1% of pixels: rendering noise, not a visual change)\n`,
  );
  for (const r of rows.changed) {
    console.log(`CHANGED  ${r.key}   [${pct(r)}; ${r.from.status} → ${r.to.status}; ${dims(r.from)} → ${dims(r.to)}]`);
    console.log(`   before: ${abs(r.from)}`);
    console.log(`   after:  ${abs(r.to)}`);
    if (wantDiff) {
      const out = path.join(diffDir, `${lib.caseKey(r.to).replace(/\.png$/i, "").replace(/[^a-zA-Z0-9._-]+/g, "_")}.png`);
      console.log(lib.writeDiffPng(abs(r.from), abs(r.to), out) ? `   diff:   ${out}` : "   diff:   (dimensions differ — compare side by side)");
    }
  }
  for (const r of rows.new) console.log(`NEW      ${r.key}   [${r.to.status}]\n   ${abs(r.to)}`);
  for (const r of rows.missing) console.log(`MISSING  ${r.key}   (last seen: ${abs(r.from)})`);
  if (args.includes("--all")) {
    for (const r of rows.minor) console.log(`minor    ${r.key}   [${pct(r)}]`);
    for (const r of rows.same) console.log(`same     ${r.key}`);
  }
}

function cmdHistory(args) {
  const text = args[0];
  if (!text) die("usage: history <text in the case key>");
  const cases = lib.buildCases(runsOrDie());
  const root = lib.archiveRoot();
  let hits = 0;
  for (const [key, history] of cases) {
    if (!key.toLowerCase().includes(text.toLowerCase())) continue;
    hits++;
    console.log(`\n${key}`);
    for (const h of history) {
      console.log(`  ${h.startedAt}  ${h.change.padEnd(7)} ${String(h.status).padEnd(8)} ${h.sha || "?"}  ${path.join(root, h.file)}`);
    }
  }
  if (!hits) die(`No case key contains '${text}'.`);
}

function cmdPromote(args) {
  const id = args[0];
  if (!id) die("usage: promote <runId> [--force]");
  const run = findRun(runsOrDie(), id);
  const root = lib.archiveRoot();
  const docShots = (run.shots || []).filter((s) => s.docName);
  if (!docShots.length) die(`Run ${run.runId} has no wiki shots (none were captured with a docName).`);

  const failing = docShots.filter((s) => !["passed", "imported"].includes(s.status));
  if (failing.length && !args.includes("--force")) {
    die(
      `Refusing: ${failing.length} wiki shot(s) come from tests that did not pass:\n  ` +
        failing.map((s) => `${s.docName} (${s.status})`).join("\n  ") +
        "\nFix the tests, or pass --force if the images are still right.",
    );
  }

  fs.mkdirSync(lib.DOCS_DIR, { recursive: true });
  for (const s of docShots) fs.copyFileSync(path.join(root, s.file), path.join(lib.DOCS_DIR, s.docName));
  const promotionsFile = path.join(root, "promotions.json");
  const promotions = lib.readJson(promotionsFile, []);
  promotions.push({
    runId: run.runId,
    promotedAt: new Date().toISOString(),
    git: lib.gitInfo(),
    forced: failing.length > 0,
    files: docShots.map((s) => s.docName).sort(),
  });
  fs.writeFileSync(promotionsFile, JSON.stringify(promotions, null, 2));
  lib.buildIndex(root);
  console.log(`Promoted ${docShots.length} image(s) from ${run.runId} into ${path.relative(lib.REPO_ROOT, lib.DOCS_DIR)}.`);
  console.log("Review with `git diff --stat docs/help/assets/screenshots` and commit them like any doc change.");
}

function cmdImport(args) {
  const label = arg(args, "--label");
  const dir = arg(args, "--dir");
  if (!label || !dir) die("usage: import --label <label> --dir <folder of PNGs> [--doc] [--sha <sha>] [--when <iso>] [--source-note <text>]");
  if (!fs.existsSync(dir)) die(`No such folder: ${dir}`);
  const pngs = fs.readdirSync(dir).filter((f) => f.toLowerCase().endsWith(".png")).sort();
  if (!pngs.length) die(`No PNGs in ${dir}.`);

  const root = lib.archiveRoot();
  const when = arg(args, "--when", new Date().toISOString());
  const stamp = new Date(when).toISOString().replace(/\.\d+Z$/, "Z").replace(/:/g, "-");
  const runId = `${stamp}_${label}`;
  if (lib.loadRuns(root).some((r) => r.runId === runId)) die(`Run ${runId} already exists — imports never overwrite.`);
  const isDoc = args.includes("--doc");
  const target = path.join(root, "runs", runId, "imported");
  fs.mkdirSync(target, { recursive: true });

  const git = lib.gitInfo();
  const shots = pngs.map((f) => {
    const dest = path.join(target, f);
    fs.copyFileSync(path.join(dir, f), dest);
    const { width, height } = lib.pngSize(dest);
    return {
      name: f.replace(/\.png$/i, ""),
      docName: isDoc ? f : null,
      project: "imported",
      spec: label,
      titlePath: [],
      retry: 0,
      url: null,
      fullPage: null,
      capturedAt: null,
      file: path.relative(root, dest).split(path.sep).join("/"),
      status: "imported",
      sha256: lib.sha256(dest),
      pixelHash: lib.pixelHash(dest),
      width,
      height,
      bytes: fs.statSync(dest).size,
    };
  });
  lib.writeRun(
    {
      runId,
      source: "import",
      label,
      note: arg(args, "--source-note", null),
      startedAt: new Date(when).toISOString(),
      finishedAt: new Date(when).toISOString(),
      result: "imported",
      // An imported image's branch is unknown — never stamp the CURRENT branch onto an old image.
      git: { sha: arg(args, "--sha", git.sha), branch: arg(args, "--branch", null), dirty: false },
      tests: null,
      shots,
    },
    root,
  );
  lib.buildIndex(root);
  console.log(`Imported ${shots.length} PNG(s) as run ${runId}.`);
}

const [command, ...args] = process.argv.slice(2);
const commands = { index: cmdIndex, compare: cmdCompare, history: cmdHistory, promote: cmdPromote, import: cmdImport };
if (!commands[command]) {
  die("usage: shots.cjs <index | compare | history | promote | import>  (see the header of this file)");
}
commands[command](args);
