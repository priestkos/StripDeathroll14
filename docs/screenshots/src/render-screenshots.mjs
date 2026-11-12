import { chromium } from "playwright";
import { mkdir } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const outDir = path.resolve(__dirname, "..");

const scenes = [
  { html: "01-setup-venue-players.html", png: "01-setup-venue-players.png" },
  { html: "02-turns-roll-for-me.html", png: "02-turns-roll-for-me.png" },
  { html: "03-wrong-roll-alert.html", png: "03-wrong-roll-alert.png" },
  { html: "04-who-strips.html", png: "04-who-strips.png" },
  { html: "05-history.html", png: "05-history.png" },
  { html: "06-rules-and-ad.html", png: "06-rules-and-ad.png" },
];

await mkdir(outDir, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage();
await page.setViewportSize({ width: 1280, height: 720 });

for (const { html, png } of scenes) {
  const fileUrl = `file://${path.join(__dirname, html)}`;
  await page.goto(fileUrl, { waitUntil: "load" });
  await page.waitForTimeout(100);
  await page.screenshot({
    path: path.join(outDir, png),
    type: "png",
  });
  console.log("Wrote", png);
}

await browser.close();
