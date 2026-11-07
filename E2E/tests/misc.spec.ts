import { expect, test } from "@playwright/test";
import { createClass, joinClass, signInAs, unique } from "./helpers";

test("teacher creates, filters, edits and deletes a library question", async ({ page }) => {
  await signInAs(page, "teacher");
  const text = unique("Library question");

  await page.goto("/ContentLibrary/Create");
  await page.locator("#questionEditor").fill(text);
  await page.locator('input[name="answerKey"]').fill("42");
  await page.getByRole("button", { name: "Save Question" }).click();
  await expect(page).toHaveURL(/\/ContentLibrary\/?(\?.*)?$/);
  await expect(page.getByText(text).first()).toBeVisible();

  await page.locator('input[name="search"]').fill(text);
  await page.locator('input[name="search"]').press("Enter");
  await expect(page.getByText(text).first()).toBeVisible();

  await page.locator('a[href*="/ContentLibrary/Edit/"]').first().click();
  await expect(page.locator("#questionEditor")).toContainText(text);
  await page.locator('input[name="answerKey"]').fill("43");
  await page.getByRole("button", { name: /Save/ }).first().click();
  await expect(page).toHaveURL(/\/ContentLibrary\/?(\?.*)?$/);

  page.once("dialog", d => d.accept());
  await page.locator('form[action*="/ContentLibrary/Delete/"] button[type="submit"]').first().click();
  await expect(page.getByText(text)).toHaveCount(0);
});

test("profile can be edited and persists", async ({ page }) => {
  await signInAs(page, "teacher");
  await page.goto("/Profile");
  await page.locator("#editBtn").click();
  const school = unique("E2E School");
  await page.locator('input[name="school"]').fill(school);
  await page.locator('textarea[name="bio"]').fill("Bio from the E2E suite");
  await page.getByRole("button", { name: /Save/ }).first().click();
  await page.goto("/Profile");
  await expect(page.locator("body")).toContainText(school);
});

test("student pages load without server errors", async ({ page }) => {
  await signInAs(page, "student");
  for (const path of ["/Student", "/Student/Classes", "/Student/Assignments", "/Profile"]) {
    const response = await page.goto(path);
    expect(response?.status(), path).toBeLessThan(400);
  }
});

test("teacher pages all render without server errors", async ({ page }) => {
  await signInAs(page, "teacher");
  for (const path of ["/", "/Classes", "/Classes/Create", "/Classes/Students", "/Assignments",
    "/Assignments/Create", "/ContentLibrary", "/ContentLibrary/Create", "/Analytics", "/Profile"]) {
    const response = await page.goto(path);
    expect(response?.status(), path).toBeLessThan(400);
  }
});

test("class creation rejects a duplicate class name", async ({ page }) => {
  await signInAs(page, "teacher");
  const name = unique("E2E Duplicate");
  await createClass(page, name);

  await page.goto("/Classes/Create");
  await page.locator('input[name="name"]').fill(name);
  await page.locator('input[name="subject"]').fill("Mathematics");
  await page.locator('select[name="gradeLevel"]').selectOption("8");
  await page.locator('select[name="academicYear"]').selectOption("2026-2027");
  await page.getByRole("button", { name: "Create Class" }).click();
  await expect(page).toHaveURL(/\/Classes\/Create/);
  await expect(page.locator(".alert-danger, .text-danger").first()).toBeVisible();
});

test("student sees class after join and the class shows up on teacher roster", async ({ browser }) => {
  const teacher = await (await browser.newContext()).newPage();
  const student = await (await browser.newContext()).newPage();
  try {
    await signInAs(teacher, "teacher");
    await signInAs(student, "student");
    const name = unique("E2E Roster Check");
    const code = await createClass(teacher, name);
    await joinClass(student, code, name);
    await teacher.goto("/Classes/Students");
    await expect(teacher.locator("tbody")).toContainText("E2E Student");
    await teacher.locator('select[name="classId"]').selectOption({ label: name });
    await teacher.locator('form[method="get"] button[type="submit"]').click();
    await expect(teacher.locator("tbody")).toContainText("E2E Student");
  } finally {
    await Promise.all([teacher.context().close(), student.context().close()]);
  }
});
