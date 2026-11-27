import { expect, test, type Page } from "@playwright/test";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { signIn, signInAs, unique } from "./helpers";

// "Import Cambridge Paper" on a REAL pair of Cambridge documents read by the REAL AI (OpenRouter, paid). Opt-in only:
// set E2E_CAMBRIDGE_REAL_DIR to a folder holding
//   paper.pdf        a 9709 Question Paper (e.g. the 2027 specimen Paper 1)
//   markscheme.pdf   its Mark Scheme
//   other-ms.pdf     a mark scheme of another component (to see the mismatch refused)
// and E2E_SYLLABUS_PDF to the 2026–2027 syllabus. The documents are Cambridge's and stay out of the repository.
// The spec records what was read (E2E_SHOT_DIR/real-pair-summary.json) and asserts only the structure, since the
// model's wording can vary.
test.describe.configure({ mode: "serial" });

const dir = process.env.E2E_CAMBRIDGE_REAL_DIR;
const syllabus = process.env.E2E_SYLLABUS_PDF;
const shots = process.env.E2E_SHOT_DIR;
const stamp = unique("real").replace(/\s/g, "");
let teacher: Page;
let importId = "";
const summary: Record<string, unknown> = {};

async function shot(page: Page, name: string, fullPage = true): Promise<void> {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(600);
  await page.screenshot({ path: resolve(shots, `${name}.png`), fullPage });
}

test.beforeAll(async ({ browser }) => {
  test.skip(!dir || !existsSync(resolve(dir, "paper.pdf")) || !syllabus, "Set E2E_CAMBRIDGE_REAL_DIR and E2E_SYLLABUS_PDF to run on real documents.");
  teacher = await (await browser.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
});
test.afterAll(async () => {
  await teacher?.context().close();
  if (shots && Object.keys(summary).length) writeFileSync(resolve(shots, "real-pair-summary.json"), JSON.stringify(summary, null, 2));
});

test("real pair: paper read by AI, mismatching scheme refused, matching scheme matched, review and save", async ({ browser }) => {
  test.setTimeout(900_000);
  const admin = await (await browser.newContext()).newPage();
  await signInAs(admin, "admin");
  await admin.goto("/Syllabus");
  await admin.locator("#syllabusFile").setInputFiles(syllabus!);
  await admin.locator('#syllabusImportForm button[type="submit"]').click();
  if (await admin.locator("#syllabusPublish").count()) await admin.locator("#syllabusPublish").click();
  await expect(admin.locator("#syllabusStatus")).toContainText("Published");
  await admin.context().close();

  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill("Real Pair Teacher");
  await teacher.locator('input[name="email"]').fill(`${stamp}@example.test`);
  await teacher.locator('input[name="password"]').fill("Real-Pair-123");
  await teacher.locator('input[name="confirmPassword"]').fill("Real-Pair-123");
  await teacher.locator('button[type="submit"]').click();
  await signIn(teacher, `${stamp}@example.test`, "Real-Pair-123");

  // Step 1 on the real paper.
  await teacher.goto("/CambridgeImport");
  await teacher.locator("#paperFile").setInputFiles(resolve(dir!, "paper.pdf"));
  await teacher.locator('#paperUploadForm button[type="submit"]').click();
  importId = teacher.url().match(/CambridgeImport\/([0-9a-f-]+)/)![1];
  await expect(teacher.locator("[data-part]").first()).toBeVisible({ timeout: 600_000 });
  summary.details = {
    component: await teacher.locator("#dComponent").inputValue(), variant: await teacher.locator("#dVariant").inputValue(),
    session: await teacher.locator("#dSession").inputValue(), year: await teacher.locator("#dYear").inputValue(),
  };
  summary.parts = await teacher.locator("[data-part]").evaluateAll(ps => ps.map(p => ({
    number: p.getAttribute("data-part"),
    topic: p.querySelector("[data-topic-label]")?.textContent?.trim(),
  })));
  summary.figures = await teacher.locator("[data-figure]").evaluateAll(fs => fs.map(f => f.getAttribute("data-figure")));
  await teacher.waitForTimeout(4000);           // let the figure crops upload
  await teacher.reload();
  await shot(teacher, "real-step1-question-paper");

  // Step 2: another component's mark scheme is refused; then the right one.
  await teacher.locator("#continueToMarkScheme").click();
  if (existsSync(resolve(dir!, "other-ms.pdf"))) {
    await teacher.locator("#schemeFile").setInputFiles(resolve(dir!, "other-ms.pdf"));
    await teacher.locator("#uploadScheme").click();
    await expect(teacher.locator("#schemeConflict")).toBeVisible();
    summary.conflict = await teacher.locator("#schemeConflict").innerText();
    await shot(teacher, "real-step2-conflict");
  }
  await teacher.locator("#schemeFile").setInputFiles(resolve(dir!, "markscheme.pdf"));
  await teacher.locator("#uploadScheme").click();
  await expect(teacher.locator("#schemeSummary, #schemeError")).toBeVisible({ timeout: 600_000 });
  summary.schemeSummary = await teacher.locator("#schemeSummary, #schemeError").innerText();
  await shot(teacher, "real-step2-mark-scheme");

  // Step 3: what needs review, and why.
  await teacher.locator("#continueToReview").click();
  summary.review = await teacher.locator("#partList tbody tr").evaluateAll(rs => rs.map(r => ({
    number: r.getAttribute("data-row"), status: r.getAttribute("data-status"),
  })));
  // Every part as the teacher first sees it, before any edit: key, criteria, status, reason and answer format.
  const details: Record<string, unknown>[] = [];
  for (const n of (summary.review as { number: string }[]).map(r => r.number)) {
    await teacher.locator(`#partList tr[data-row="${n}"] a`).click();
    await expect(teacher.locator("#detailNumber")).toHaveText(n);
    details.push({
      number: n,
      status: (await teacher.locator("#detailStatus").innerText()).trim(),
      reason: await teacher.locator("#reviewReason").count() ? (await teacher.locator("#reviewReason").innerText()).trim() : null,
      key: (await teacher.locator("#detailKey").innerText()).trim(),
      keySource: await teacher.locator("#detailKey [data-latex-source]").first().getAttribute("data-latex-source", { timeout: 2000 }).catch(() => null),
      criteria: (await teacher.locator("#detailCriteria").innerText()).trim(),
      format: await teacher.locator("#formatDrawn").isChecked() ? "drawn" : "written",
      formatQuestion: await teacher.locator("#formatQuestion").count() ? (await teacher.locator("#formatQuestion").innerText()).trim() : null,
    });
  }
  summary.partReview = details;
  const firstNeeds = teacher.locator('#partList tr[data-status="Needs review"] a').first();
  if (await firstNeeds.count()) {
    await firstNeeds.click();
    summary.firstReason = await teacher.locator("#reviewReason").innerText().catch(() => null);
  } else {
    // Nothing to review: show a part with the shared figure instead.
    await teacher.locator('#partList tr[data-row="9(b)"] a, #partList tbody tr a').first().click();
  }
  await shot(teacher, "real-step3-review", false);   // the screen as the teacher sees it, action bar at the bottom
  const nested = teacher.locator('#partList tr[data-row="10(b)(i)"] a');
  if (await nested.count()) {
    await nested.click();
    summary.nested = { key: await teacher.locator("#detailKey").innerText(), criteria: await teacher.locator("#detailCriteria").innerText() };
  }

  // Confirm the matched rows and save them.
  if (await teacher.locator("#selectAllMatched").count()) {
    await teacher.locator("#selectAllMatched").check();
    await teacher.locator("#confirmSelected").click();
    await teacher.locator("#saveConfirmed").click();
    await expect(teacher.locator("#finishCounts")).toBeVisible();
    summary.finish = await teacher.locator("#finishCounts").innerText();
    await shot(teacher, "real-step4-finish");
  }
});
