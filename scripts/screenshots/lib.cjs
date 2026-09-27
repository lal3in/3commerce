// Screenshot history — shared by the Playwright reporter (src/Storefront/e2e-support/screenshot-reporter.ts)
// and the CLI (scripts/screenshots/shots.cjs). Plain CommonJS with no dependencies so both can require it.
//
// Layout (test-artifacts/screenshots/, gitignored — local history, never pruned by tooling):
//   runs/<runId>/run.json                       one record per Playwright invocation (or import)
//   runs/<runId>/<project>/<spec>/<test>/<shot>.png + .json sidecar
//   promotions.json                             every time a run was copied into the wiki
//   index.json / INDEX.md                       regenerated from the above; safe to delete and rebuild
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const { execSync } = require("node:child_process");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const DOCS_DIR = path.join(REPO_ROOT, "docs", "help", "assets", "screenshots");

function archiveRoot() {
  return process.env.SCREENSHOT_ARCHIVE || path.join(REPO_ROOT, "test-artifacts", "screenshots");
}

function git(args, fallback = "") {
  try {
    return execSync(`git ${args}`, { cwd: REPO_ROOT, stdio: ["ignore", "pipe", "ignore"] }).toString().trim();
  } catch {
    return fallback;
  }
}

function gitInfo() {
  return {
    sha: git("rev-parse --short HEAD", "unknown"),
    branch: git("rev-parse --abbrev-ref HEAD", "unknown"),
    dirty: git("status --porcelain") !== "",
  };
}

/** Sortable, filesystem-safe, and says which commit it was taken on: 2026-09-27T10-15-22Z_a1b2c3d[-dirty]. */
function newRunId(label) {
  const stamp = new Date().toISOString().replace(/\.\d+Z$/, "Z").replace(/:/g, "-");
  const { sha, dirty } = gitInfo();
  return `${stamp}_${label || sha}${dirty && !label ? "-dirty" : ""}`;
}

function sha256(file) {
  return crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
}

/** Width/height straight from the PNG IHDR chunk — no image library needed. */
function pngSize(file) {
  const fd = fs.openSync(file, "r");
  try {
    const buf = Buffer.alloc(24);
    fs.readSync(fd, buf, 0, 24, 0);
    if (buf.toString("ascii", 12, 16) !== "IHDR") return { width: null, height: null };
    return { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) };
  } finally {
    fs.closeSync(fd);
  }
}

/**
 * Minimal PNG decoder (8-bit greyscale/RGB/RGBA/grey+alpha, non-interlaced — what Chromium screenshots
 * produce) using only node:zlib. Returns RGBA pixels. Exists because comparing FILE bytes is meaningless for
 * screenshots: the encoder and invisible sub-pixel rendering make two identical-looking captures differ in
 * bytes on nearly every run, which would mark everything "changed" and bury the real changes.
 */
function decodePng(file) {
  const zlib = require("node:zlib");
  const buf = fs.readFileSync(file);
  let pos = 8;
  let width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
  const idat = [];
  while (pos < buf.length) {
    const len = buf.readUInt32BE(pos);
    const type = buf.toString("ascii", pos + 4, pos + 8);
    const data = buf.subarray(pos + 8, pos + 8 + len);
    if (type === "IHDR") {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bitDepth = data[8];
      colorType = data[9];
      interlace = data[12];
    } else if (type === "IDAT") {
      idat.push(data);
    } else if (type === "IEND") {
      break;
    }
    pos += 12 + len;
  }
  const channels = { 0: 1, 2: 3, 4: 2, 6: 4 }[colorType];
  if (bitDepth !== 8 || !channels || interlace !== 0) return null; // unsupported → caller falls back to bytes

  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  const out = Buffer.alloc(width * height * 4);
  let prev = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let x = 0; x < stride; x++) {
      const a = x >= channels ? line[x - channels] : 0;
      const b = prev[x];
      const c = x >= channels ? prev[x - channels] : 0;
      let add = 0;
      if (filter === 1) add = a;
      else if (filter === 2) add = b;
      else if (filter === 3) add = (a + b) >> 1;
      else if (filter === 4) {
        const p = a + b - c;
        const pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
        add = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
      }
      line[x] = (line[x] + add) & 0xff;
    }
    for (let x = 0; x < width; x++) {
      const s = x * channels, d = (y * width + x) * 4;
      if (channels >= 3) {
        out[d] = line[s]; out[d + 1] = line[s + 1]; out[d + 2] = line[s + 2];
        out[d + 3] = channels === 4 ? line[s + 3] : 255;
      } else {
        out[d] = out[d + 1] = out[d + 2] = line[s];
        out[d + 3] = channels === 2 ? line[s + 1] : 255;
      }
    }
    prev = line;
  }
  return { width, height, pixels: out };
}

/** Hash of what the image LOOKS like (dimensions + decoded pixels), independent of PNG encoding. */
function pixelHash(file) {
  const img = decodePng(file);
  if (!img) return `bytes:${sha256(file)}`;
  return crypto.createHash("sha256").update(`${img.width}x${img.height}:`).update(img.pixels).digest("hex");
}

/**
 * How different two screenshots look: the share of pixels whose colour moved by more than a small
 * tolerance (absorbs anti-aliasing noise). Different dimensions count as a full change.
 */
function pixelDiff(fileA, fileB, tolerance = 24) {
  const a = decodePng(fileA);
  const b = decodePng(fileB);
  if (!a || !b) return { ratio: sha256(fileA) === sha256(fileB) ? 0 : 1, diffPixels: null, total: null, comparable: false };
  if (a.width !== b.width || a.height !== b.height) {
    return { ratio: 1, diffPixels: null, total: null, comparable: false, resized: true };
  }
  let diff = 0;
  const total = a.width * a.height;
  for (let i = 0; i < a.pixels.length; i += 4) {
    const d = Math.max(
      Math.abs(a.pixels[i] - b.pixels[i]),
      Math.abs(a.pixels[i + 1] - b.pixels[i + 1]),
      Math.abs(a.pixels[i + 2] - b.pixels[i + 2]),
    );
    if (d > tolerance) diff++;
  }
  return { ratio: diff / total, diffPixels: diff, total, comparable: true };
}

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function encodePng(width, height, rgba) {
  const zlib = require("node:zlib");
  const chunk = (type, data) => {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));
    return Buffer.concat([len, body, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  const raw = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) rgba.copy(raw, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4);
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr),
    chunk("IDAT", zlib.deflateSync(raw)),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

/**
 * A diff image: the AFTER image washed out to pale grey, with every pixel that moved painted solid red.
 * Returns false when the two cannot be overlaid (different dimensions or an unsupported PNG).
 */
function writeDiffPng(fileA, fileB, out, tolerance = 24) {
  const a = decodePng(fileA);
  const b = decodePng(fileB);
  if (!a || !b || a.width !== b.width || a.height !== b.height) return false;
  const px = Buffer.alloc(b.pixels.length);
  for (let i = 0; i < px.length; i += 4) {
    const moved = Math.max(
      Math.abs(a.pixels[i] - b.pixels[i]),
      Math.abs(a.pixels[i + 1] - b.pixels[i + 1]),
      Math.abs(a.pixels[i + 2] - b.pixels[i + 2]),
    ) > tolerance;
    if (moved) {
      px[i] = 230; px[i + 1] = 0; px[i + 2] = 0;
    } else {
      const grey = Math.round(0.3 * b.pixels[i] + 0.59 * b.pixels[i + 1] + 0.11 * b.pixels[i + 2]);
      const faded = Math.round(grey * 0.35 + 255 * 0.65);
      px[i] = px[i + 1] = px[i + 2] = faded;
    }
    px[i + 3] = 255;
  }
  fs.mkdirSync(path.dirname(out), { recursive: true });
  fs.writeFileSync(out, encodePng(b.width, b.height, px));
  return true;
}

/** same (pixel-identical) · minor (≤0.1% of pixels — rendering noise) · changed (a real visual change). */
function classify(ratio) {
  if (ratio === 0) return "same";
  return ratio <= 0.001 ? "minor" : "changed";
}

/**
 * The identity used to compare a shot across runs. A wiki shot is keyed by its wiki file name, so a
 * live run and an imported baseline of the same image line up; everything else is keyed by where it
 * was taken (project › spec › test › shot name).
 */
function caseKey(shot) {
  if (shot.docName) return `docs/${shot.docName}`;
  const title = (shot.titlePath || []).join(" › ");
  return [shot.project, shot.spec, title, shot.name].filter(Boolean).join(" › ");
}

function readJson(file, fallback) {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8"));
  } catch {
    return fallback;
  }
}

function loadRuns(root = archiveRoot()) {
  const runsDir = path.join(root, "runs");
  if (!fs.existsSync(runsDir)) return [];
  return fs
    .readdirSync(runsDir)
    .map((id) => readJson(path.join(runsDir, id, "run.json"), null))
    .filter(Boolean)
    .sort((a, b) => (a.startedAt || "").localeCompare(b.startedAt || "") || a.runId.localeCompare(b.runId));
}

function writeRun(run, root = archiveRoot()) {
  const dir = path.join(root, "runs", run.runId);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, "run.json"), JSON.stringify(run, null, 2));
}

/**
 * Fill in a pixel hash for any shot recorded without one (older runs/imports) and persist it back into
 * that run.json — additive enrichment only; nothing recorded is ever removed or rewritten.
 */
function ensurePixelHashes(runs, root) {
  for (const run of runs) {
    let touched = false;
    for (const shot of run.shots || []) {
      if (shot.pixelHash) continue;
      const abs = path.join(root, shot.file);
      if (!fs.existsSync(abs)) continue;
      shot.pixelHash = pixelHash(abs);
      touched = true;
    }
    if (touched) writeRun(run, root);
  }
}

/**
 * Every case with its full chronological history. Each entry is compared with the one before it:
 * new · same (pixel-identical) · minor (≤0.1% pixels, rendering noise) · changed — plus the share of
 * pixels that moved. Pixel diffs are cached in diffcache.json, so each pair is decoded at most once ever.
 */
function buildCases(runs, root = archiveRoot()) {
  const cacheFile = path.join(root, "diffcache.json");
  const cache = readJson(cacheFile, {});
  let cacheDirty = false;
  const cases = new Map();
  for (const run of runs) {
    for (const shot of run.shots || []) {
      const key = caseKey(shot);
      const history = cases.get(key) || [];
      const prev = history[history.length - 1];
      let change = "new";
      let diffRatio = null;
      let resized = false;
      if (prev) {
        if (prev.pixelHash && prev.pixelHash === shot.pixelHash) {
          change = "same";
          diffRatio = 0;
        } else {
          const pair = `${prev.file}|${shot.file}`;
          // Cache entries were bare ratios in the first version; normalise so both shapes read the same.
          const cached = typeof cache[pair] === "number" ? null : cache[pair];
          if (!cached) {
            const a = path.join(root, prev.file);
            const b = path.join(root, shot.file);
            const d = fs.existsSync(a) && fs.existsSync(b) ? pixelDiff(a, b) : { ratio: 1, resized: false };
            cache[pair] = { ratio: d.ratio, resized: Boolean(d.resized) };
            cacheDirty = true;
          }
          diffRatio = cache[pair].ratio;
          resized = cache[pair].resized;
          change = resized ? "changed" : classify(diffRatio);
        }
      }
      history.push({
        runId: run.runId,
        startedAt: run.startedAt,
        sha: run.git?.sha,
        source: run.source,
        status: shot.status,
        file: shot.file,
        pixelHash: shot.pixelHash,
        width: shot.width,
        height: shot.height,
        change,
        diffRatio,
        resized,
        prevSize: prev ? `${prev.width}×${prev.height}` : null,
      });
      cases.set(key, history);
    }
  }
  if (cacheDirty) fs.writeFileSync(cacheFile, JSON.stringify(cache, null, 2));
  return cases;
}

function summarizeRun(run, cases) {
  const counts = { shots: 0, new: 0, changed: 0, minor: 0, same: 0, failedTestShots: 0 };
  for (const shot of run.shots || []) {
    counts.shots++;
    const entry = cases.get(caseKey(shot)).find((h) => h.runId === run.runId && h.file === shot.file);
    if (entry) counts[entry.change]++;
    if (shot.status && !["passed", "imported"].includes(shot.status)) counts.failedTestShots++;
  }
  return counts;
}

function buildIndex(root = archiveRoot()) {
  const runs = loadRuns(root);
  ensurePixelHashes(runs, root);
  const cases = buildCases(runs, root);
  const promotions = readJson(path.join(root, "promotions.json"), []);

  const index = {
    generatedAt: new Date().toISOString(),
    archive: path.relative(REPO_ROOT, root).split(path.sep).join("/"),
    runs: runs.map((r) => ({
      runId: r.runId,
      startedAt: r.startedAt,
      finishedAt: r.finishedAt,
      source: r.source,
      label: r.label || null,
      git: r.git,
      result: r.result,
      tests: r.tests,
      ...summarizeRun(r, cases),
    })),
    promotions,
    cases: [...cases.entries()]
      .map(([key, history]) => ({ key, history }))
      .sort((a, b) => a.key.localeCompare(b.key)),
  };

  fs.mkdirSync(root, { recursive: true });
  fs.writeFileSync(path.join(root, "index.json"), JSON.stringify(index, null, 2));
  fs.writeFileSync(path.join(root, "INDEX.md"), renderMarkdown(index));
  return index;
}

function renderMarkdown(index) {
  const lines = [];
  lines.push("# Screenshot history");
  lines.push("");
  lines.push("> Generated by `scripts/screenshots/lib.cjs` — do not edit by hand. Rebuild with");
  lines.push("> `node scripts/screenshots/shots.cjs index`. Local and gitignored; tooling never deletes a run.");
  lines.push("");
  lines.push("**Read this first (agents):** every Playwright run gets its own folder under `runs/`. A *case* is one");
  lines.push("screenshot identity followed across runs — keyed `docs/<file>` for wiki images, otherwise");
  lines.push("`project › spec › test › shot`. Each history entry is compared with the previous run's image by decoded");
  lines.push("PIXELS (not file bytes): `same` (identical), `minor` (≤0.1% of pixels — rendering noise), or `changed`, with");
  lines.push("the share of pixels that moved. To compare two runs:");
  lines.push("`node scripts/screenshots/shots.cjs compare [--from <run>] [--to <run>] [--diff]` (defaults: previous → latest;");
  lines.push("`--diff` also writes a red-highlight diff image per changed pair), then open the paths it prints. A run can be");
  lines.push("named by any unique fragment of its id (label, sha). History of one case: `shots.cjs history <text>`. The wiki images only");
  lines.push("change via `shots.cjs promote <runId>`, which is recorded below.");
  lines.push("");

  lines.push("## Promoted to the wiki");
  lines.push("");
  if (!index.promotions.length) {
    lines.push("_Nothing promoted yet — the wiki still shows the images committed in git._");
  } else {
    lines.push("| When (UTC) | Run | Commit at promotion | Files |");
    lines.push("|---|---|---|---|");
    for (const p of [...index.promotions].reverse()) {
      lines.push(`| ${p.promotedAt} | \`${p.runId}\` | ${p.git?.sha || "?"} | ${p.files.length} |`);
    }
  }
  lines.push("");

  lines.push("## Runs (newest first)");
  lines.push("");
  lines.push("| Run | Source | Commit | Branch | Result | Tests (pass/fail/skip) | Shots | New | Changed | Minor | Same | Shots from failing tests |");
  lines.push("|---|---|---|---|---|---|---|---|---|---|---|---|");
  for (const r of [...index.runs].reverse()) {
    const t = r.tests || {};
    const tests = r.source === "import" ? "—" : `${t.passed ?? 0}/${t.failed ?? 0}/${t.skipped ?? 0}`;
    const commit = `${r.git?.sha || "?"}${r.git?.dirty ? " (dirty)" : ""}`;
    lines.push(
      `| \`${r.runId}\` | ${r.source}${r.label ? `: ${r.label}` : ""} | ${commit} | ${r.git?.branch || "?"} | ${r.result || "—"} | ${tests} | ${r.shots} | ${r.new} | ${r.changed} | ${r.minor} | ${r.same} | ${r.failedTestShots} |`,
    );
  }
  lines.push("");

  lines.push("## Cases");
  lines.push("");
  lines.push("| Case | Runs | Latest status | Latest vs previous | Pixels moved | Latest file |");
  lines.push("|---|---|---|---|---|---|");
  for (const c of index.cases) {
    const last = c.history[c.history.length - 1];
    // Paths are relative to this file's own folder (the archive root), so they open directly from here.
    // A different size cannot be overlaid pixel-for-pixel — say so rather than print a meaningless 100%.
    const moved = last.resized
      ? `resized ${last.prevSize} → ${last.width}×${last.height}`
      : last.diffRatio == null ? "—" : `${(last.diffRatio * 100).toFixed(2)}%`;
    lines.push(`| ${c.key} | ${c.history.length} | ${last.status} | ${last.change} | ${moved} | \`${last.file}\` |`);
  }
  lines.push("");
  return lines.join("\n");
}

module.exports = {
  REPO_ROOT,
  DOCS_DIR,
  archiveRoot,
  gitInfo,
  newRunId,
  sha256,
  pngSize,
  decodePng,
  pixelHash,
  pixelDiff,
  classify,
  writeDiffPng,
  caseKey,
  readJson,
  loadRuns,
  writeRun,
  buildCases,
  buildIndex,
};
