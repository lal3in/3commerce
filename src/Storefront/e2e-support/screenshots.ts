import fs from "node:fs";
import path from "node:path";
import { test, type Locator, type Page } from "@playwright/test";
import { SCREENSHOT_ATTACHMENT } from "./constants";

/**
 * The ONE way an E2E spec takes a screenshot. Nothing is ever overwritten or deleted: every Playwright
 * invocation gets its own run folder (SCREENSHOT_RUN_ID, set once in playwright.config.ts and inherited by
 * the workers), and within it every shot is keyed by project → spec → test → shot name → retry. The
 * reporter (screenshot-reporter.ts) pairs each shot with its test's outcome and rebuilds the history index
 * at test-artifacts/screenshots/INDEX.md, so any later run can be compared with any earlier one.
 *
 * The wiki's images (docs/help/assets/screenshots) are NOT written here. A shot that illustrates the wiki
 * passes `docName`; it only reaches the wiki when someone deliberately promotes a run:
 *   node scripts/screenshots/shots.cjs promote <runId>
 */
export type CaptureOptions = {
  /** Full scrollable page (default true). Ignored for a Locator, which is captured at its own bounds. */
  fullPage?: boolean;
  /** Set only for shots that illustrate the wiki: the file name under docs/help/assets/screenshots/. */
  docName?: string;
};

export function slug(value: string): string {
  return value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 80) || "untitled";
}

function isPage(target: Page | Locator): target is Page {
  return typeof (target as Page).goto === "function";
}

export async function capture(target: Page | Locator, name: string, options: CaptureOptions = {}): Promise<string> {
  const info = test.info();
  const root = process.env.SCREENSHOT_ARCHIVE;
  const runId = process.env.SCREENSHOT_RUN_ID;
  if (!root || !runId) {
    throw new Error("SCREENSHOT_ARCHIVE / SCREENSHOT_RUN_ID are unset — run through playwright.config.ts.");
  }

  const project = info.project.name;
  // Spec path relative to the storefront root (e.g. "e2e/screenshots.spec.ts"), stable across machines.
  const spec = path.relative(path.resolve(__dirname, ".."), info.file).split(path.sep).join("/");
  // titlePath is [file, ...describe blocks, test title]; drop the file — it is already the spec.
  const titlePath = info.titlePath.slice(1);
  const retrySuffix = info.retry > 0 ? `.retry${info.retry}` : "";

  const dir = path.join(root, "runs", runId, project, slug(spec.replace(/\.spec\.ts$/, "")), slug(titlePath.join(" ")));
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${slug(name)}${retrySuffix}.png`);

  const page = isPage(target) ? target : target.page();
  if (isPage(target)) {
    await target.screenshot({ path: file, fullPage: options.fullPage ?? true });
  } else {
    await target.screenshot({ path: file });
  }

  const meta = {
    name,
    docName: options.docName ?? null,
    project,
    spec,
    titlePath,
    retry: info.retry,
    url: page.url(),
    fullPage: isPage(target) ? (options.fullPage ?? true) : false,
    capturedAt: new Date().toISOString(),
    file: path.relative(root, file).split(path.sep).join("/"),
  };
  // A sidecar next to the PNG makes every file self-describing even without the index…
  fs.writeFileSync(file.replace(/\.png$/, ".json"), JSON.stringify(meta, null, 2));
  // …and the attachment is how the reporter learns which test (and which outcome) the shot belongs to.
  await info.attach(SCREENSHOT_ATTACHMENT, { body: JSON.stringify(meta), contentType: "application/json" });
  return file;
}
