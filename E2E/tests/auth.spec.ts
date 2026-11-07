import { expect, test } from "@playwright/test";
import { accounts, createClass, signIn, signInAs, unique } from "./helpers";

test("login rejects a wrong password and unknown email", async ({ page }) => {
  await signIn(page, accounts.teacher.email, "definitely-wrong-password");
  await expect(page.getByText("Invalid email or password.")).toBeVisible();

  await signIn(page, "nobody@example.test", "whatever-123");
  await expect(page.getByText("Invalid email or password.")).toBeVisible();
});

test("anonymous visitors are sent to login for protected pages", async ({ page }) => {
  for (const path of ["/Classes", "/Assignments", "/Student", "/Admin", "/Profile", "/ContentLibrary"]) {
    await page.goto(path);
    await expect(page, `${path} should require login`).toHaveURL(/\/Auth\/Login/);
  }
});

test("forgot password page explains who can reset", async ({ page }) => {
  await page.goto("/Auth/ForgotPassword");
  await expect(page.locator("body")).toContainText(/teacher|administrator/i);
});

test("each role lands on its own home page and logout ends the session", async ({ page }) => {
  await signInAs(page, "teacher");
  await expect(page).toHaveURL(/\/$/);
  await page.goto("/Auth/Logout");
  await expect(page).toHaveURL(/\/Auth\/Login/);
  await page.goto("/Classes");
  await expect(page).toHaveURL(/\/Auth\/Login/);

  await signInAs(page, "student");
  await expect(page).toHaveURL(/\/Student\/?$/);
  await page.goto("/Auth/Logout");

  await signInAs(page, "admin");
  await page.goto("/Admin");
  await expect(page.getByRole("heading", { name: /Admin Panel/ })).toBeVisible();
  await expect(page.getByText("E2E Teacher", { exact: true })).toBeVisible();
});

test("student cannot open admin or content library, and sees no teacher class data", async ({ page }) => {
  await signInAs(page, "student");
  for (const path of ["/Admin", "/ContentLibrary"]) {
    await page.goto(path);
    await expect(page, `${path} should bounce the student`).not.toHaveURL(new RegExp(`${path}/?$`));
  }

  // Teacher class pages are closed to students: they are sent back to their own home page.
  await page.goto("/Classes");
  await expect(page).not.toHaveURL(/\/Classes\/?$/);
  await expect(page).toHaveURL(/\/Student\/?$/);
});

test("teacher cannot open the admin panel", async ({ page }) => {
  await signInAs(page, "teacher");
  await page.goto("/Admin");
  await expect(page.getByRole("heading", { name: /Admin Panel/ })).toHaveCount(0);
});

test("new teacher registers, signs in and sees an empty dashboard", async ({ page }) => {
  const email = `${unique("new.teacher").replace(/\s/g, "")}@example.test`;
  const password = "Teacher-Pass-123";

  await page.goto("/Auth/Register");
  await page.locator('input[name="fullName"]').fill("Brand New Teacher");
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('input[name="confirmPassword"]').fill(password);
  await page.locator('button[type="submit"]').click();
  await expect(page).toHaveURL(/\/Auth\/Login/);

  await signIn(page, email, password);
  await expect(page.getByRole("heading", { name: "Dashboard" })).toBeVisible();
});

test("registration validates mismatched and short passwords and duplicate email", async ({ page }) => {
  await page.goto("/Auth/Register");
  await page.locator('input[name="fullName"]').fill("Bad Pass");
  await page.locator('input[name="email"]').fill("bad.pass@example.test");
  await page.locator('input[name="password"]').fill("longenough1");
  await page.locator('input[name="confirmPassword"]').fill("different123");
  await page.locator('button[type="submit"]').click();
  await expect(page.getByText("Passwords do not match.")).toBeVisible();

  await page.locator('input[name="fullName"]').fill("Bad Pass");
  await page.locator('input[name="email"]').fill("bad.pass@example.test");
  await page.locator('input[name="password"]').fill("short");
  await page.locator('input[name="confirmPassword"]').fill("short");
  await page.locator('button[type="submit"]').click();
  await expect(page.getByText("Password must be at least 8 characters.")).toBeVisible();

  await page.locator('input[name="fullName"]').fill("Dup");
  await page.locator('input[name="email"]').fill(accounts.teacher.email);
  await page.locator('input[name="password"]').fill("Another-Pass-123");
  await page.locator('input[name="confirmPassword"]').fill("Another-Pass-123");
  await page.locator('button[type="submit"]').click();
  await expect(page.locator(".alert-danger, .text-danger").first()).toBeVisible();
});

test("new student self-registers with an invite code and lands in that class", async ({ page, browser }) => {
  const teacherPage = await browser.newPage();
  const className = unique("E2E Register Class");
  try {
    await signInAs(teacherPage, "teacher");
    const inviteCode = await createClass(teacherPage, className);

    const email = `${unique("new.student").replace(/\s/g, "")}@example.test`;
    const password = "Student-Pass-123";
    await page.goto("/Auth/StudentRegister");
    await page.locator('input[name="inviteCode"]').fill(inviteCode);
    await page.locator('input[name="fullName"]').fill("Brand New Student");
    await page.locator('input[name="email"]').fill(email);
    await page.locator('input[name="password"]').fill(password);
    await page.locator('input[name="confirmPassword"]').fill(password);
    await page.locator('button[type="submit"]').click();
    await expect(page).toHaveURL(/\/Auth\/Login/);

    await signIn(page, email, password);
    await expect(page).toHaveURL(/\/Student\/?$/);
    await expect(page.getByText(className, { exact: true }).first()).toBeVisible();
  } finally {
    await teacherPage.close();
  }
});

test("student registration rejects an invalid invite code", async ({ page }) => {
  await page.goto("/Auth/StudentRegister");
  await page.locator('input[name="inviteCode"]').fill("ZZZZZZ");
  await page.locator('input[name="fullName"]').fill("No Class");
  await page.locator('input[name="email"]').fill(`${unique("noclass").replace(/\s/g, "")}@example.test`);
  await page.locator('input[name="password"]').fill("Student-Pass-123");
  await page.locator('input[name="confirmPassword"]').fill("Student-Pass-123");
  await page.locator('button[type="submit"]').click();
  await expect(page).toHaveURL(/\/Auth\/StudentRegister/);
  await expect(page.locator(".alert-danger, .text-danger").first()).toBeVisible();
});
