import fs from "node:fs";
import path from "node:path";
import type { FullConfig, FullResult, Reporter, Suite, TestCase, TestResult } from "@playwright/test/reporter";
import { SCREENSHOT_ATTACHMENT } from "./constants";

// The shared index builder lives with the CLI so both use one implementation. It is plain CommonJS on
// purpose: this reporter is transpiled by Playwright, the CLI runs on bare Node, and both can require it.
// eslint-disable-next-line @typescript-eslint/no-require-imports
const lib = require(path.resolve(__dirname, "..", "..", "..", "scripts", "screenshots", "lib.cjs"));

type Shot = {
  name: string;
  docName: string | null;
  project: string;
  spec: string;
  titlePath: string[];
  retry: number;
  url: string;
  fullPage: boolean;
  capturedAt: string;
  file: string;
  status?: string;
  sha256?: string;
  pixelHash?: string;
  width?: number | null;
  height?: number | null;
  bytes?: number;
};

/**
 * Records every screenshot taken through capture() against the outcome of the test that took it, writes
 * runs/<runId>/run.json when the run ends, and rebuilds INDEX.md / index.json across ALL runs. Runs in the
 * Playwright main process only, so there is no cross-worker write race.
 */
export default class ScreenshotReporter implements Reporter {
  private startedAt = new Date().toISOString();
  private shots: Shot[] = [];
  private tests = { passed: 0, failed: 0, skipped: 0, flaky: 0 };
  private projects = new Set<string>();

  onBegin(_config: FullConfig, suite: Suite) {
    this.startedAt = new Date().toISOString();
    for (const test of suite.allTests()) {
      const project = test.parent.project()?.name;
      if (project) this.projects.add(project);
    }
  }

  onTestEnd(test: TestCase, result: TestResult) {
    // Count final outcomes only (a retried test ends several times; its last attempt decides).
    const isFinal = result.status === "passed" || result.retry >= test.retries;
    if (isFinal) {
      if (result.status === "passed") this.tests[result.retry > 0 ? "flaky" : "passed"]++;
      else if (result.status === "skipped") this.tests.skipped++;
      else this.tests.failed++;
    }

    const root = process.env.SCREENSHOT_ARCHIVE;
    if (!root) return;
    for (const attachment of result.attachments) {
      if (attachment.name !== SCREENSHOT_ATTACHMENT || !attachment.body) continue;
      const shot = JSON.parse(attachment.body.toString("utf8")) as Shot;
      const abs = path.join(root, shot.file);
      if (!fs.existsSync(abs)) continue; // the screenshot call itself failed — nothing to record
      const { width, height } = lib.pngSize(abs);
      this.shots.push({
        ...shot,
        status: result.status,
        sha256: lib.sha256(abs),
        pixelHash: lib.pixelHash(abs),
        width,
        height,
        bytes: fs.statSync(abs).size,
      });
    }
  }

  onEnd(result: FullResult) {
    const root = process.env.SCREENSHOT_ARCHIVE;
    const runId = process.env.SCREENSHOT_RUN_ID;
    if (!root || !runId) return;
    // A run that took no screenshots (e.g. a --grep for one API-only spec) adds nothing worth indexing.
    if (this.shots.length === 0) return;

    lib.writeRun(
      {
        runId,
        source: "playwright",
        startedAt: this.startedAt,
        finishedAt: new Date().toISOString(),
        result: result.status,
        git: lib.gitInfo(),
        host: process.env.HOSTNAME || process.env.COMPUTERNAME || null,
        ci: Boolean(process.env.CI),
        projects: [...this.projects].sort(),
        tests: this.tests,
        shots: this.shots,
      },
      root,
    );
    const index = lib.buildIndex(root);
    const run = index.runs.find((r: { runId: string }) => r.runId === runId);
    console.log(
      `\nScreenshots: ${this.shots.length} archived in run ${runId} ` +
        `(${run?.new ?? 0} new, ${run?.changed ?? 0} changed, ${run?.minor ?? 0} minor, ${run?.same ?? 0} same) — ` +
        `${path.relative(process.cwd(), path.join(root, "INDEX.md"))}`,
    );
  }
}
