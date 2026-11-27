import { expect, test, type Page } from "@playwright/test";
import { resolve } from "node:path";
import { addQuestion, createClass, createDraftAssignment, publishAndAssignAll, signIn, submitAssignment, unique } from "./helpers";

// The notifications bell and the avatar menu, end to end in the browser:
//  - a student hands in work → the teacher's bell shows it, with a link to the submission; opening the bell marks
//    it as seen; the teacher checks the work → the student's bell says so and leads to the result;
//  - the profile photo can be changed and removed from the avatar menu on any page.
test.describe.configure({ mode: "serial" });

const password = "Bell-Avatar-123";
const stamp = unique("bell").replace(/\s/g, "");
const className = unique("Bell class");
let teacher: Page;
let student: Page;
let assignmentId = "";

async function register(page: Page, name: string, email: string, invite?: string): Promise<void> {
  await page.goto(invite ? "/Auth/StudentRegister" : "/Auth/Register");
  if (invite) await page.locator('input[name="inviteCode"]').fill(invite);
  await page.locator('input[name="fullName"]').fill(name);
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('input[name="confirmPassword"]').fill(password);
  await page.locator('button[type="submit"]').click();
  await signIn(page, email, password);
}
const bell = (page: Page) => page.locator("#notifications");
const badge = (page: Page) => page.locator("#notificationsBadge");

test.beforeAll(async ({ browser }) => {
  teacher = await (await browser.newContext()).newPage();
  student = await (await browser.newContext()).newPage();
  student.on("dialog", d => void d.accept());
});
test.afterAll(async () => { await teacher?.context().close(); await student?.context().close(); });

test("a new account has a quiet bell: no made-up notifications", async () => {
  await register(teacher, "Bell Teacher", `${stamp}.teacher@example.test`);
  await teacher.goto("/");
  await expect(badge(teacher)).toBeHidden();
  await bell(teacher).locator("#notificationsToggle").click();
  await expect(bell(teacher).locator("#notificationsEmpty")).toBeVisible();
  await expect(bell(teacher).locator("[data-notification]")).toHaveCount(0);
});

test("the student is told about a new assignment; the teacher about the work handed in", async () => {
  test.setTimeout(120_000);
  const invite = await createClass(teacher, className);
  assignmentId = await createDraftAssignment(teacher, `Bell practice ${stamp.slice(-4)}`, className);
  await addQuestion(teacher, { kind: "Text/Numeric", text: "Bell: 2 plus 2", answerKey: "4" });
  await register(student, `Sam ${stamp.slice(-4)}`, `${stamp}.student@example.test`, invite);
  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  await publishAndAssignAll(teacher, assignmentId);

  // Student: one new item, the assignment; it opens the assignment.
  await student.goto("/");
  await expect(badge(student)).toHaveText("1");
  await bell(student).locator("#notificationsToggle").click();
  const assigned = bell(student).locator('[data-notification="assigned"]');
  await expect(assigned).toContainText("New assignment");
  await expect(assigned).toContainText(`Bell practice ${stamp.slice(-4)}`);
  await expect(badge(student)).toBeHidden();                                     // opened: seen
  await assigned.locator("a").click();
  await expect(student).toHaveURL(new RegExp(`/Student/TakeAssignment\\?assignmentId=${assignmentId}`, "i"));
  await student.waitForLoadState("load");
  await student.evaluate(() => customElements.whenDefined("math-field"));         // the answer field is ready
  await student.locator('.nav-btn[data-idx="0"]').click();
  await student.locator('.answer-textarea[data-qkey="q_0"]').fill("4");
  await submitAssignment(student);
  await expect(student).toHaveURL(/AssignmentResult/, { timeout: 60_000 });
  await student.reload();
  await expect(badge(student)).toBeHidden();                                     // stays seen after a reload

  // Teacher: the hand-in, linked to the submission.
  await teacher.goto("/");
  await expect(badge(teacher)).toHaveText("1");
  await bell(teacher).locator("#notificationsToggle").click();
  const handedIn = bell(teacher).locator('[data-notification="submitted"], [data-notification="review"]').first();
  await expect(handedIn).toContainText(`Sam ${stamp.slice(-4)}`);
  await expect(handedIn).toHaveClass(/notification-new/);
  await expect(badge(teacher)).toBeHidden();
  await handedIn.locator("a").click();
  await expect(teacher).toHaveURL(/\/Assignments\/SubmissionDetail\?resultId=/);
});

test("the teacher checks the work; the student is told and is taken to the result", async () => {
  await teacher.getByRole("button", { name: /Save grades/i }).click();
  await expect(teacher.getByText("Grades saved.")).toBeVisible();

  await student.goto("/");
  await expect(badge(student)).toHaveText("1");
  await bell(student).locator("#notificationsToggle").click();
  const checked = bell(student).locator('[data-notification="checked"]');
  await expect(checked).toContainText("Your teacher checked your work");
  await checked.locator("a").click();
  await expect(student).toHaveURL(/\/Student\/AssignmentResult\?assignmentId=.*&resultId=/);
  // The page left open picks up the count by itself (here: nothing new).
  expect((await (await student.request.get("/Notifications/Count")).json()).unread).toBe(0);
});

test("the profile photo is changed and removed from the avatar menu, back on the same page", async () => {
  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  const avatar = teacher.locator(".dropdown-user > a img");
  await expect(avatar).toHaveAttribute("src", "/sneat/img/avatars/1.png");
  await teacher.locator(".dropdown-user > a").click();
  await expect(teacher.locator("#navRemovePhoto")).toHaveCount(0);              // nothing to remove yet
  const chooser = teacher.waitForEvent("filechooser");
  await teacher.locator("#navChangePhoto").click();
  await (await chooser).setFiles(resolve(__dirname, "../../wwwroot/sneat/img/avatars/5.png"));
  await expect(teacher).toHaveURL(new RegExp(`/Assignments/Details/${assignmentId}`, "i"));
  await expect(avatar).toHaveAttribute("src", /\/uploads\/avatars\//);
  expect(await avatar.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);

  await teacher.locator(".dropdown-user > a").click();
  await teacher.locator("#navRemovePhoto").click();
  await expect(teacher).toHaveURL(new RegExp(`/Assignments/Details/${assignmentId}`, "i"));
  await expect(avatar).toHaveAttribute("src", "/sneat/img/avatars/1.png");
});
