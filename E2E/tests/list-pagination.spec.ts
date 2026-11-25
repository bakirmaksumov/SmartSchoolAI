import { expect, test, type Page } from "@playwright/test";
import { createClass, createDraftAssignment, signIn, unique } from "./helpers";

// Assignments and Content Library show ten items per page; the pager keeps the filters.
// A brand-new teacher, so the counts are exact: 11 assignments and 11 library questions.
test.describe.configure({ mode: "serial" });

const stamp = unique("pager").replace(/\s/g, "");
const email = `${stamp}@example.test`;
const password = "Pager-Pass-123";
let page: Page;

test.beforeAll(async ({ browser }) => {
  page = await (await browser.newContext()).newPage();
  page.on("dialog", d => void d.accept());
  await page.goto("/Auth/Register");
  await page.locator('input[name="fullName"]').fill("Pager Teacher");
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('input[name="confirmPassword"]').fill(password);
  await page.locator('button[type="submit"]').click();
  await expect(page).toHaveURL(/\/Auth\/Login/);
  await signIn(page, email, password);
});

test.afterAll(async () => { await page.context().close(); });

const rows = () => page.locator("table tbody tr");
const summary = () => page.locator("[data-pager-summary]");

test("Assignments: ten per page, the second page has the rest, filters survive paging", async () => {
  test.setTimeout(240_000);
  const className = unique("Pager class");
  await createClass(page, className);
  for (let i = 1; i <= 11; i++) await createDraftAssignment(page, `${stamp} A${String(i).padStart(2, "0")}`, className);

  await page.goto("/Assignments");
  await expect(rows()).toHaveCount(10);
  await expect(summary()).toHaveText("Showing 1–10 of 11");
  // Newest first: the last one created is on page 1, the first one created on page 2.
  await expect(page.getByText(`${stamp} A11`)).toBeVisible();
  await expect(page.getByText(`${stamp} A01`)).toHaveCount(0);

  await page.locator(".pagination").getByRole("link", { name: "2", exact: true }).click();
  await expect(page).toHaveURL(/[?&]page=2/);
  await expect(rows()).toHaveCount(1);
  await expect(page.getByText(`${stamp} A01`)).toBeVisible();
  await expect(summary()).toHaveText("Showing 11–11 of 11");

  // A filter keeps the page links filtered; choosing a filter starts again at page 1.
  await page.locator('select[name="status"]').selectOption("Draft");
  await expect(page).not.toHaveURL(/page=/);
  await page.locator(".pagination").getByRole("link", { name: "Next page" }).click();
  await expect(page).toHaveURL(/status=Draft/);
  await expect(page).toHaveURL(/page=2/);
  await expect(rows()).toHaveCount(1);

  // A page past the end shows the last page, not an empty list.
  await page.goto("/Assignments?page=99");
  await expect(rows()).toHaveCount(1);
});

test("Content Library: ten per page; deleting the only item on page 2 lands on the remaining page", async () => {
  test.setTimeout(240_000);
  for (let i = 1; i <= 11; i++) {
    await page.goto("/ContentLibrary/Create");
    await page.locator("#questionEditor").fill(`${stamp} Q${String(i).padStart(2, "0")}`);
    await page.locator('input[name="answerKey"]').fill(String(i));
    await page.getByRole("button", { name: "Save Question" }).click();
    await expect(page).toHaveURL(/\/ContentLibrary\/?(\?.*)?$/);
  }

  await page.goto("/ContentLibrary");
  await expect(page.getByText("11 questions found")).toBeVisible();
  await expect(rows()).toHaveCount(10);
  await expect(summary()).toHaveText("Showing 1–10 of 11");

  await page.locator(".pagination").getByRole("link", { name: "Next page" }).click();
  await expect(rows()).toHaveCount(1);
  await expect(page.getByText(`${stamp} Q01`)).toBeVisible();

  // Deleting the only question on page 2: back on the library, which is now one page (no pager).
  await page.locator('form[action*="/ContentLibrary/Delete/"] button[type="submit"]').first().click();
  await expect(page.getByText("10 questions found")).toBeVisible();
  await expect(rows()).toHaveCount(10);
  await expect(page.locator(".pagination")).toHaveCount(0);
});

test("Students: ten per page; a search starts again at page 1", async () => {
  test.setTimeout(300_000);
  const rosterClass = unique("Roster class");
  const invite = await createClass(page, rosterClass);
  for (let i = 1; i <= 11; i++) {
    const p = await (await page.context().browser()!.newContext()).newPage();
    await p.goto("/Auth/StudentRegister");
    await p.locator('input[name="inviteCode"]').fill(invite);
    await p.locator('input[name="fullName"]').fill(`${stamp} Pupil ${String(i).padStart(2, "0")}`);
    await p.locator('input[name="email"]').fill(`${stamp}.p${i}@example.test`);
    await p.locator('input[name="password"]').fill(password);
    await p.locator('input[name="confirmPassword"]').fill(password);
    await p.locator('button[type="submit"]').click();
    await expect(p).toHaveURL(/\/Auth\/Login/);
    await p.context().close();
  }

  await page.goto("/Classes/Students");
  await expect(page.locator("#rosterCount")).toHaveText("11 students");
  await expect(rows()).toHaveCount(10);
  await expect(summary()).toHaveText("Showing 1–10 of 11");
  await page.locator(".pagination").getByRole("link", { name: "Next page" }).click();
  await expect(page).toHaveURL(/page=2/);
  await expect(rows()).toHaveCount(1);
  await expect(page.getByText(`${stamp} Pupil 11`)).toBeVisible();

  // A search narrows the list and starts again at page 1; no pager when it fits on one page.
  await page.locator('input[name="search"]').fill(`${stamp} Pupil 0`);
  await page.locator('input[name="search"]').press("Enter");
  await expect(page).not.toHaveURL(/page=/);
  await expect(page.locator("#rosterCount")).toHaveText("9 students");
  await expect(page.locator(".pagination")).toHaveCount(0);
});
