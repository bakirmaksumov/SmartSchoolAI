import { expect, test } from "@playwright/test";
import { createClass, signIn, signInAs, unique } from "./helpers";

test.describe.configure({ mode: "serial" });

const className = unique("E2E Manage Class");
const studentEmail = `${unique("roster.student").replace(/\s/g, "")}@example.test`;
let classId = "";

test("teacher class page: invite code, edit settings, archive and restore", async ({ browser }) => {
  const page = await (await browser.newContext()).newPage();
  try {
    await signInAs(page, "teacher");
    await createClass(page, className);

    const card = page.locator(".card").filter({ has: page.getByRole("heading", { name: className, exact: true }) });
    await card.getByRole("link").first().click();
    await expect(page).toHaveURL(/\/Classes\/Details\/[0-9a-f-]+/i);
    classId = page.url().match(/\/Classes\/Details\/([0-9a-f-]+)/i)![1];

    for (const tab of ["assignments", "results", "students", "settings"]) {
      await page.goto(`/Classes/Details/${classId}?tab=${tab}`);
      await expect(page.locator("body")).toContainText(className);
    }

    await page.goto(`/Classes/Details/${classId}?tab=settings`);
    const renamed = `${className} Renamed`;
    await page.locator('form[action*="/Classes/Edit/"] input[name="name"]').fill(renamed);
    await page.locator('form[action*="/Classes/Edit/"] button[type="submit"]').click();
    await expect(page.locator("body")).toContainText(renamed);

    await page.goto(`/Classes/Details/${classId}?tab=settings`);
    page.once("dialog", d => d.accept());
    await page.locator('form[action*="/Classes/Archive/"] button[type="submit"]').click();
    await page.goto("/Classes");
    await expect(page.getByRole("heading", { name: renamed, exact: true })).toHaveCount(0);
    await page.goto("/Classes?archived=true");
    await expect(page.getByRole("heading", { name: renamed, exact: true })).toBeVisible();

    await page.goto(`/Classes/Details/${classId}?tab=settings`);
    await page.locator('form[action*="/Classes/Restore/"] button[type="submit"]').click();
    await page.goto("/Classes");
    await expect(page.getByRole("heading", { name: renamed, exact: true })).toBeVisible();
  } finally {
    await page.context().close();
  }
});

test("class settings: co-teacher validation and accessibility save", async ({ browser }) => {
  const page = await (await browser.newContext()).newPage();
  try {
    await signInAs(page, "teacher");
    await page.goto(`/Classes/Details/${classId}?tab=settings`);

    await page.locator('form[action*="/Classes/AddCoTeacher"] input[name="email"]').fill("nobody.here@example.test");
    await page.locator('form[action*="/Classes/AddCoTeacher"] button[type="submit"]').click();
    await expect(page).toHaveURL(new RegExp(`/Classes/Details/${classId}`, "i"));

    await page.goto(`/Classes/Details/${classId}?tab=settings`);
    await page.locator("#voiceEnabled").check();
    await expect(page).toHaveURL(new RegExp(`/Classes/Details/${classId}`, "i"));
    await page.goto(`/Classes/Details/${classId}?tab=settings`);
    await expect(page.locator("#voiceEnabled")).toBeChecked();
  } finally {
    await page.context().close();
  }
});

test("teacher manages a student: roster, edit, reset password, student forced to change it, remove", async ({ browser }) => {
  const teacher = await (await browser.newContext()).newPage();
  const student = await (await browser.newContext()).newPage();
  try {
    await signInAs(teacher, "teacher");
    await teacher.goto(`/Classes/Details/${classId}?tab=assignments`);
    await teacher.goto("/Classes");
    const code = (await teacher.locator(".card").filter({ has: teacher.getByRole("heading", { name: /Renamed/ }) })
      .locator(".badge.bg-label-info").first().innerText()).match(/[A-Z0-9]{6}/)![0];

    await student.goto("/Auth/StudentRegister");
    await student.locator('input[name="inviteCode"]').fill(code);
    await student.locator('input[name="fullName"]').fill("Roster Student");
    await student.locator('input[name="email"]').fill(studentEmail);
    await student.locator('input[name="password"]').fill("Student-Pass-123");
    await student.locator('input[name="confirmPassword"]').fill("Student-Pass-123");
    await student.locator('button[type="submit"]').click();
    await expect(student).toHaveURL(/\/Auth\/Login/);

    await teacher.goto(`/Classes/Details/${classId}?tab=students`);
    await expect(teacher.locator("strong.d-block", { hasText: "Roster Student" })).toBeVisible();

    await teacher.goto("/Classes/Students");
    await teacher.locator('input[name="search"]').fill("Roster");
    await teacher.locator('form[method="get"] button[type="submit"]').click();
    await expect(teacher.getByText("Roster Student").first()).toBeVisible();

    await teacher.goto(`/Classes/Details/${classId}?tab=students`);
    await teacher.locator("tr", { hasText: "Roster Student" }).locator('[data-bs-toggle="dropdown"]').click();
    await teacher.getByRole("button", { name: /Edit student/ }).click();
    await teacher.locator("#editStudentName").fill("Roster Student Renamed");
    await teacher.locator("#editStudentModal").getByRole("button", { name: /Save changes/ }).click();
    await expect(teacher.locator("strong.d-block", { hasText: "Roster Student Renamed" })).toBeVisible();

    await teacher.locator("tr", { hasText: "Roster Student Renamed" }).locator('[data-bs-toggle="dropdown"]').click();
    await teacher.getByRole("button", { name: /Reset password/ }).first().click();
    await teacher.locator("#resetPasswordInput").fill("Brand-New-Pass-456");
    await teacher.locator("#resetPasswordModal").getByRole("button", { name: /Reset password/ }).click();
    await expect(teacher.locator("body")).toContainText("Roster Student Renamed");

    await signIn(student, studentEmail, "Student-Pass-123");
    await expect(student.getByText("Invalid email or password.")).toBeVisible();

    await signIn(student, studentEmail, "Brand-New-Pass-456");
    await expect(student).toHaveURL(/\/Auth\/ChangePassword/);
    await student.locator("#newPassword").fill("Final-Pass-789");
    await student.locator("#confirmPassword").fill("Final-Pass-789");
    await student.locator('button[type="submit"]').click();
    await expect(student).toHaveURL(/\/Student\/?$/);

    await teacher.goto(`/Classes/Details/${classId}?tab=students`);
    teacher.once("dialog", d => d.accept());
    await teacher.locator("tr", { hasText: "Roster Student Renamed" }).locator('[data-bs-toggle="dropdown"]').click();
    await teacher.getByRole("button", { name: /Remove student/ }).click();
    await expect(teacher.locator("strong.d-block", { hasText: "Roster Student Renamed" })).toHaveCount(0);
  } finally {
    await Promise.all([teacher.context().close(), student.context().close()]);
  }
});

test("admin resets a teacher password and the teacher signs in with it", async ({ browser }) => {
  const admin = await (await browser.newContext()).newPage();
  const teacher = await (await browser.newContext()).newPage();
  const email = `${unique("pw.teacher").replace(/\s/g, "")}@example.test`;
  try {
    await teacher.goto("/Auth/Register");
    await teacher.locator('input[name="fullName"]').fill("Password Teacher");
    await teacher.locator('input[name="email"]').fill(email);
    await teacher.locator('input[name="password"]').fill("Teacher-Pass-123");
    await teacher.locator('input[name="confirmPassword"]').fill("Teacher-Pass-123");
    await teacher.locator('button[type="submit"]').click();

    await signInAs(admin, "admin");
    await admin.goto("/Admin");
    await admin.goto(`/Admin?search=${encodeURIComponent(email)}`);
    await admin.locator("tr", { hasText: email }).getByRole("button", { name: /Reset password/ }).click();
    await admin.locator("#resetPasswordInput").fill("Reset-By-Admin-1");
    await admin.locator("#resetPasswordModal").getByRole("button", { name: /Reset password/ }).click();

    await signIn(teacher, email, "Reset-By-Admin-1");
    await expect(teacher).toHaveURL(/\/Auth\/ChangePassword/);
    await teacher.locator("#newPassword").fill("Teacher-Final-123");
    await teacher.locator("#confirmPassword").fill("Teacher-Final-123");
    await teacher.locator('button[type="submit"]').click();
    await expect(teacher.getByRole("heading", { name: "Dashboard" })).toBeVisible();
  } finally {
    await Promise.all([admin.context().close(), teacher.context().close()]);
  }
});

test("teacher cannot open another teacher's class", async ({ browser }) => {
  const other = await (await browser.newContext()).newPage();
  try {
    const email = `${unique("other.teacher").replace(/\s/g, "")}@example.test`;
    await other.goto("/Auth/Register");
    await other.locator('input[name="fullName"]').fill("Other Teacher");
    await other.locator('input[name="email"]').fill(email);
    await other.locator('input[name="password"]').fill("Teacher-Pass-123");
    await other.locator('input[name="confirmPassword"]').fill("Teacher-Pass-123");
    await other.locator('button[type="submit"]').click();
    await signIn(other, email, "Teacher-Pass-123");

    const response = await other.request.get(`/Classes/Details/${classId}`);
    expect(response.status()).toBe(404);
    await other.goto("/Classes");
    await expect(other.getByText(className)).toHaveCount(0);
  } finally {
    await other.context().close();
  }
});

