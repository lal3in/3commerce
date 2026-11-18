import { test, expect } from "./fixtures";
import type { Page, Request } from "../e2e-support/fixtures";
import { capture } from "../e2e-support/screenshots";

/**
 * Storefront observability, live (audit P1-2 Faro RUM + P1-3 SSR error pages; owner decisions 2026-11-15):
 *  - the error pages show a support reference (server digest / SSR trace id), localized;
 *  - Faro initialises on a real page and posts to the same-origin `/rum` relay;
 *  - what the browser sends to `/rum` carries no identifiers (email, user id, session id, query strings) even when
 *    the page deliberately offers them, and same-origin fetches carry `traceparent`;
 *  - nothing is sent from a Stripe iframe, Faro never starts in a frame, and Stripe requests get no `traceparent`;
 *  - the relay itself fails soft, caps the payload and refuses cross-site posts;
 *  - browser source maps are not served (production build).
 * Needs the storefront only (any data seed). The `/rum` relay is intercepted where the payload is asserted, so it
 * does not depend on the collector's faro receiver being deployed.
 */

const EMAIL = "jane.doe+rum@example.com";
const USER_ID = "user-e2e-4242";
const SESSION_ID = "sess-e2e-1717";

type FaroWindow = Window & {
  faro?: {
    api: {
      pushError: (e: Error) => void;
      pushEvent: (name: string, attrs?: Record<string, string>) => void;
      setUser: (u: Record<string, string>) => void;
      setSession: (s: Record<string, unknown>) => void;
    };
  };
};

async function waitForFaro(page: Page): Promise<void> {
  await page.waitForFunction(() => Boolean((window as FaroWindow).faro?.api), undefined, { timeout: 20_000 });
}

/** Captures every `/rum` POST (body + headers + frame) and answers it locally with 202. */
async function interceptRum(page: Page) {
  const sent: Array<{ body: string; headers: Record<string, string>; fromMainFrame: boolean }> = [];
  await page.route("**/rum", async (route) => {
    const request = route.request();
    if (request.method() === "POST") {
      let fromMainFrame = true;
      try {
        fromMainFrame = request.frame() === page.mainFrame();
      } catch {
        fromMainFrame = false;
      }
      sent.push({ body: request.postData() ?? "", headers: request.headers(), fromMainFrame });
    }
    await route.fulfill({ status: 202, body: "" });
  });
  return sent;
}

test.describe("Storefront observability (Faro RUM + error pages)", () => {
  test("a server render error shows the error page with its digest as the support reference", async ({ page }) => {
    const response = await page.goto("/probe/error");
    expect(response?.status()).toBe(500);
    await expect(page.getByRole("heading", { name: "Something went wrong" })).toBeVisible();
    const reference = page.getByTestId("error-reference");
    await expect(reference).toHaveText(/^[0-9A-Za-z]{6,}$/);
    // Never the raw error message (components.md §5).
    await expect(page.getByText("Deliberate storefront error probe")).toHaveCount(0);
    await capture(page, "error-page-server-reference");
  });

  test("the error page is localized (de) and a browser render error shows the SSR trace id", async ({ page, context, baseURL }) => {
    await context.addCookies([{ name: "3c_locale", value: "de", url: baseURL ?? "http://localhost:3000" }]);
    await page.goto("/probe/client-error");
    await expect(page.getByRole("heading", { name: "Etwas ist schiefgelaufen" })).toBeVisible();
    await expect(page.getByText("Support-Referenz")).toBeVisible();
    const reference = (await page.getByTestId("error-reference").textContent())?.trim() ?? "";
    const traceparent = await page.locator('meta[name="traceparent"]').getAttribute("content").catch(() => null);
    if (traceparent) {
      expect(reference).toBe(traceparent.split("-")[1]); // the SSR render's trace id
    } else {
      expect(reference).toMatch(/^[0-9a-f]{16}$/); // no tracer registered: a random, still-reported reference
    }
  });

  test("Faro initialises and posts to the same-origin /rum relay", async ({ page }) => {
    const rumPost = page.waitForRequest((r) => new URL(r.url()).pathname === "/rum" && r.method() === "POST", {
      timeout: 30_000,
    });
    await page.goto("/");
    await waitForFaro(page);
    await page.evaluate(() => (window as FaroWindow).faro!.api.pushEvent("e2e-faro-initialised"));
    const request = await rumPost;
    expect(new URL(request.url()).origin).toBe(new URL(page.url()).origin);
    const body = JSON.parse(request.postData() ?? "{}");
    expect(body.meta?.app?.name).toBe("storefront");
    expect(body.meta?.app?.version).toMatch(/^\d+\.\d+\.\d+/); // service.version from the build's public env
    expect(body.meta?.app?.environment).toBeTruthy();
  });

  test("the /rum payload holds no email, user id, session id or query string", async ({ page }) => {
    const sent = await interceptRum(page);
    const traceparents: string[] = [];
    page.on("request", (r: Request) => {
      if (new URL(r.url()).pathname.startsWith("/api/order-status/")) traceparents.push(r.headers()["traceparent"] ?? "");
    });

    await page.goto(`/search?q=${encodeURIComponent(EMAIL)}&token=qs-secret-token`);
    await waitForFaro(page);
    await page.evaluate(
      async ({ email, userId, sessionId }) => {
        const faro = (window as FaroWindow).faro!;
        // Everything a careless integration could hand Faro — none of it may leave the page.
        faro.api.setUser({ id: userId, email, username: email });
        faro.api.setSession({ id: sessionId });
        console.error(`checkout failed for ${email} token=console-secret`);
        faro.api.pushError(new Error(`e2e-rum-probe for ${email} at /orders/x?email=${encodeURIComponent(email)}`));
        await fetch(`/api/order-status/00000000-0000-7000-8000-000000000000?email=${encodeURIComponent(email)}`).catch(
          () => undefined,
        );
      },
      { email: EMAIL, userId: USER_ID, sessionId: SESSION_ID },
    );

    await expect.poll(() => sent.some((s) => s.body.includes("e2e-rum-probe")), { timeout: 30_000 }).toBe(true);
    const all = sent.map((s) => s.body).join("\n");
    for (const leaked of [
      EMAIL,
      encodeURIComponent(EMAIL),
      USER_ID,
      SESSION_ID,
      "qs-secret-token",
      "console-secret",
      "?q=",
      "?email=",
      "userAgent",
      "x-faro-session-id",
    ]) {
      expect(all, `leaked "${leaked}"`).not.toContain(leaked);
    }
    for (const s of sent) {
      const body = JSON.parse(s.body);
      expect(body.meta?.session).toBeUndefined();
      expect(body.meta?.user).toBeUndefined();
      expect(s.headers["x-faro-session-id"]).toBeUndefined();
    }
    // The browser → backend link: a same-origin fetch carries a W3C traceparent.
    expect(traceparents.length).toBeGreaterThan(0);
    expect(traceparents[0]).toMatch(/^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$/);
  });

  test("nothing is sent from a Stripe iframe; Stripe requests carry no traceparent", async ({ page }) => {
    const sent = await interceptRum(page);
    const stripeRequests: Array<{ url: string; traceparent?: string }> = [];
    // A stand-in for the Stripe Elements iframe: a cross-origin document that throws, logs errors and fetches.
    await page.route(/^https:\/\/(js|api|m)\.stripe\.(com|network)\//, async (route) => {
      stripeRequests.push({ url: route.request().url(), traceparent: route.request().headers()["traceparent"] });
      const html =
        "<!doctype html><title>stripe stand-in</title><script>" +
        "console.error('stripe-iframe-probe console');" +
        "fetch('https://api.stripe.com/v1/e2e-from-iframe').catch(()=>{});" +
        "setTimeout(function(){ throw new Error('stripe-iframe-probe uncaught'); }, 0);" +
        "</script>";
      await route.fulfill({ status: 200, contentType: route.request().url().includes("/v1/") ? "application/json" : "text/html", body: route.request().url().includes("/v1/") ? "{}" : html });
    });

    await page.goto("/");
    await waitForFaro(page);
    await page.evaluate(async () => {
      const stripe = document.createElement("iframe");
      stripe.src = "https://js.stripe.com/v3/elements-inner-card.html";
      stripe.setAttribute("data-testid", "stripe-frame");
      document.body.appendChild(stripe);
      // The storefront itself, framed: Faro must not start inside it either.
      const self = document.createElement("iframe");
      self.src = "/privacy";
      self.setAttribute("data-testid", "self-frame");
      document.body.appendChild(self);
      // A top-level call to Stripe from our own page must not get our trace context.
      await fetch("https://api.stripe.com/v1/e2e-from-top").catch(() => undefined);
      (window as FaroWindow).faro!.api.pushEvent("e2e-stripe-check-done");
    });

    const selfFrame = page.frameLocator('[data-testid="self-frame"]');
    await expect(selfFrame.locator("body")).toBeVisible();
    await expect.poll(() => sent.some((s) => s.body.includes("e2e-stripe-check-done")), { timeout: 30_000 }).toBe(true);
    // Give the frames time to have sent anything they were going to.
    await page.waitForTimeout(2_000);

    const framedFaro = await page
      .frame({ url: /\/privacy$/ })
      ?.evaluate(() => Boolean((window as FaroWindow).faro))
      .catch(() => false);
    expect(framedFaro ?? false).toBe(false);
    expect(sent.every((s) => s.fromMainFrame)).toBe(true);
    expect(sent.map((s) => s.body).join("\n")).not.toContain("stripe-iframe-probe");
    expect(stripeRequests.length).toBeGreaterThan(0);
    for (const r of stripeRequests) expect(r.traceparent, r.url).toBeUndefined();
  });

  test("the /rum relay fails soft, caps the payload and refuses cross-site posts", async ({ request }) => {
    const dirty = {
      meta: { app: { name: "storefront" }, session: { id: SESSION_ID }, user: { email: EMAIL } },
      events: [{ name: "e2e-relay", attributes: { url: `/search?q=${EMAIL}` } }],
    };
    // Relayed (scrubbed) to the faro receiver when it is deployed, dropped when it is not — 202 either way.
    const ok = await request.post("/rum", { data: dirty, headers: { "x-forwarded-for": "203.0.113.9" } });
    expect(ok.status()).toBe(202);

    const tooBig = await request.post("/rum", {
      data: { meta: {}, logs: [{ message: "x".repeat(300 * 1024) }] },
    });
    expect(tooBig.status()).toBe(413);

    const malformed = await request.post("/rum", { data: Buffer.from("{not json"), headers: { "content-type": "application/json" } });
    expect(malformed.status()).toBe(400);

    const crossSite = await request.post("/rum", { data: dirty, headers: { "sec-fetch-site": "cross-site" } });
    expect(crossSite.status()).toBe(403);
  });

  test("browser source maps are not served (production build)", async ({ page, request }) => {
    await page.goto("/");
    const scripts = await page.locator("script[src]").evaluateAll((els) => els.map((e) => (e as HTMLScriptElement).src));
    const chunk = scripts.find((s) => s.includes("/_next/static/chunks/"));
    expect(chunk, "a Next chunk script").toBeTruthy();
    // `next dev` (a reused dev-up stack) serves its own dev maps; its chunks are cache-busted with ?v=.
    test.skip(scripts.some((s) => s.includes("?v=")), "dev server: production source-map guard not in play");
    const map = await request.get(`${chunk!.split("?")[0]}.map`);
    expect(map.status()).toBe(404);
  });
});
