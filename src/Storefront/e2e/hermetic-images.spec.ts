import {
  test,
  expect,
  EXTERNAL_IMAGE_HOSTS,
  STAND_IN_HEADER,
  abortExternalImages,
  isExternalSeedImage,
} from "../e2e-support/fixtures";

/**
 * Pins the shared hermetic-images fixture (e2e-support/fixtures.ts) itself. Needs no running stack: it loads
 * seed-host images into a blank page, so it proves the stand-in answers them (no network, no hang) and that a
 * spec's own page.route still wins when it wants the host to fail.
 */
test.describe("hermetic external images", () => {
  test("covers every seed image host, direct and through the next/image optimizer", () => {
    expect(EXTERNAL_IMAGE_HOSTS).toEqual(expect.arrayContaining(["picsum.photos", "placehold.co"]));
    expect(isExternalSeedImage(new URL("https://picsum.photos/seed/p-x/600/600"))).toBe(true);
    expect(isExternalSeedImage(new URL("https://placehold.co/800x600?text=X"))).toBe(true);
    expect(
      isExternalSeedImage(new URL("http://localhost:3000/_next/image?url=https%3A%2F%2Fpicsum.photos%2Fseed%2Fp%2F600%2F600&w=640&q=75")),
    ).toBe(true);
    // Our own images are never stubbed — a broken internal image must still fail the broken-image guards.
    expect(isExternalSeedImage(new URL("http://localhost:5200/img/image-placeholder.svg"))).toBe(false);
    expect(isExternalSeedImage(new URL("http://localhost:3000/_next/image?url=%2Flogo.png&w=64&q=75"))).toBe(false);
    expect(isExternalSeedImage(new URL("https://cdn.example.org/a.png"))).toBe(false);
  });

  test("a seed-host image loads the local stand-in, without the network", async ({ page }) => {
    const responses: Record<string, string | undefined> = {};
    page.on("response", (r) => (responses[r.url()] = r.headers()[STAND_IN_HEADER]));

    await page.setContent(
      `<img id="a" src="https://picsum.photos/seed/hermetic/600/600"><img id="b" src="https://placehold.co/800x600?text=H">`,
    );
    for (const id of ["a", "b"]) {
      await expect
        .poll(() => page.locator(`#${id}`).evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth > 0), {
          timeout: 5_000,
        })
        .toBe(true);
    }
    expect(Object.values(responses)).toEqual(["1", "1"]);
  });

  test("a spec's own abort still overrides the stand-in (the unreachable-host path)", async ({ page }) => {
    const aborted: string[] = [];
    await abortExternalImages(page, (url) => aborted.push(url));

    await page.setContent(`<img id="a" src="https://picsum.photos/seed/hermetic-down/600/600">`);
    await expect
      .poll(() => page.locator("#a").evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth === 0), {
        timeout: 5_000,
      })
      .toBe(true);
    expect(aborted).toHaveLength(1);
  });
});
