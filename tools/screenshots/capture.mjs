// Captures README screenshots from a running demo (docker compose up).
// Usage: docker run --rm --add-host=host.docker.internal:host-gateway -v "$PWD:/work" -w /work \
//          mcr.microsoft.com/playwright:v1.64.1-noble sh -c "npm i --no-save playwright@1.64.1 && node tools/screenshots/capture.mjs"
import { chromium } from "playwright";

const base = process.env.BASE_URL ?? "http://host.docker.internal:18440";
const out = process.env.OUT_DIR ?? "docs/images";

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });

await page.goto(`${base}/account/login`);
await page.click("button[data-user='mehmet.manager']");
await page.waitForURL("**/cases");
await page.waitForSelector("#live:not(.off)");
await page.waitForSelector(".tabulator-row");
// Work a few cases the way a handler would, so the panel shows every state.
for (const [index, action] of [[1, "resolve"], [2, "assign"], [3, "assign"]]) {
  await page.locator(".tabulator-row").nth(index).click();
  await page.waitForSelector("dialog[open] #case-detail h2");
  if (action === "resolve") {
    await page.fill("#note-text", "Hasta arandı, özür dilendi; randevusu öne alındı.");
    await page.click("#resolve-btn");
  } else {
    await page.click("#assign-btn");
  }
  await page.waitForTimeout(600);
  await page.keyboard.press("Escape");
  await page.waitForTimeout(300);
}

await page.waitForTimeout(2500); // chart + KPI fetch, live pushes
await page.screenshot({ path: `${out}/case-panel.png` });

await page.locator(".tabulator-row").first().click();
await page.waitForSelector("dialog[open] #case-detail h2");
await page.click("#reveal-btn").catch(() => {});
await page.waitForTimeout(800);
await page.screenshot({ path: `${out}/case-detail.png` });

// The patient's view, on a phone-sized viewport.
const phone = await browser.newPage({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2 });
const inbox = await (await fetch(`${base}/sim/handset?max=5000`)).json();
for (const message of inbox.reverse()) {
  const response = await phone.goto(new URL(new URL(message.link).pathname, base).toString());
  if (response.status() === 200) {
    await phone.screenshot({ path: `${out}/survey-mobile.png`, fullPage: false });
    break;
  }
}

await browser.close();
console.log("screenshots written to", out);
