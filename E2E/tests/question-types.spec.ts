import { expect, test, type Page } from "@playwright/test";
import {
  aiEnabled,
  createClass,
  createDraftAssignment,
  joinClass,
  publishAndAssignAll,
  signInAs,
  submitAssignment,
  unique
} from "./helpers";

// Every question type, "show your work", the photo-solution guards and the teacher's manual grading.
// Questions are created with the same POST the question editor sends, which is far cheaper than
// driving the editor once per type. No AI is called, except one step check when an AI key is available.
test.describe.configure({ mode: "serial" });

const types: { type: number; name: string }[] = [
  { type: 0, name: "Short answer" },
  { type: 2, name: "Open text" },
  { type: 3, name: "Math expression" },
  { type: 5, name: "Graphing" },
  { type: 6, name: "Geometry drawing" },
  { type: 7, name: "Number line" },
  { type: 8, name: "Dot plot" },
  { type: 9, name: "Table check" },
  { type: 10, name: "Table text" },
  { type: 11, name: "Vertical operation" },
  { type: 12, name: "Protractor" },
  { type: 13, name: "Fraction model" }
];
const comment = unique("Teacher comment");
const className = unique("E2E Types Class");

let teacher: Page;
let student: Page;
let assignmentId = "";
let resultId = "";

test.beforeAll(async ({ browser }) => {
  teacher = await (await browser.newContext()).newPage();
  student = await (await browser.newContext()).newPage();
  await signInAs(teacher, "teacher");
  await signInAs(student, "student");
});

test.afterAll(async () => {
  await teacher.context().close();
  await student.context().close();
});

async function addLibraryQuestion(type: number, title: string): Promise<void> {
  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const form = new FormData();
  const fields: [string, string][] = [
    ["assignmentId", assignmentId], ["title", title], ["type", String(type)], ["difficulty", "2"],
    ["options", "Alpha"], ["options", "Beta"], ["nlState", ""], ["vertOp", "+"], ["vertOperands", "12"],
    ["vertOperands", "30"], ["fracRows", "2"], ["fracCols", "4"], ["fracShaded", "3"], ["rubric", ""],
    ["graphState", ""], ["geoState", ""], ["dpState", ""], ["protState", ""], ["answerKey", "42"],
    ["explanation", ""], ["subject", ""], ["gradeLevel", ""], ["__RequestVerificationToken", token]
  ];
  for (const [key, value] of fields) form.append(key, value);

  const response = await teacher.request.post("/ContentLibrary/Create", { multipart: form as never });
  expect(response.status(), title).toBe(200);
  expect(await response.text(), `${title} should be attached to the assignment`).toContain("questionSaved");
}

test("setup: an assignment with one question of every type", async () => {
  test.setTimeout(240_000);
  const code = await createClass(teacher, className);
  await joinClass(student, code, className);

  assignmentId = await createDraftAssignment(teacher, unique("E2E Question Types"), className);
  for (const { type, name } of types) {
    await addLibraryQuestion(type, `${name} question`);
  }

  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  await expect(teacher.locator('#questionsList button[title="Remove"]')).toHaveCount(types.length);

  // The first question (short answer) needs the student to show their work.
  await teacher.locator("#requireWork_0").check();
  await teacher.waitForLoadState("load");
  await expect(teacher.locator("#requireWork_0")).toBeChecked();

  await publishAndAssignAll(teacher, assignmentId);
});

test("student opens every question type without page errors", async () => {
  const errors: string[] = [];
  student.on("pageerror", error => errors.push(error.message));

  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await expect(student.locator(".nav-btn[data-idx]")).toHaveCount(types.length);

  for (let i = 0; i < types.length; i++) {
    await student.locator(`.nav-btn[data-idx="${i}"]`).click();
    await expect(student.getByText(`${types[i].name} question`, { exact: true }), types[i].name).toBeVisible();
  }
  expect(errors, "JavaScript errors while paging through the questions").toEqual([]);
  student.removeAllListeners("pageerror");
});

test("show your work is required, steps can be typed and the optional toggle works", async () => {
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);

  const required = student.locator("#workToggle_0");
  await expect(required).toBeChecked();
  await expect(required).toBeDisabled();
  await expect(student.locator(".work-block").first()).toContainText("Required");

  await student.locator('.nav-btn[data-idx="1"]').click();
  const optional = student.locator("#workToggle_1");
  await expect(optional).not.toBeChecked();
  await optional.check();
  await expect(student.locator(".work-block").nth(1).locator(".work-panel")).toBeVisible();

  // Type a step on question 1 and check it lands in the hidden mirror that is submitted with the form.
  await student.locator('.nav-btn[data-idx="0"]').click();
  await student.locator('.type-step-add[data-qindex="0"]').click();
  await student.locator('.type-steps-list[data-qindex="0"] math-field').first().evaluate((el: HTMLElement & { value: string }) => {
    el.value = "2+2=4";
    el.dispatchEvent(new Event("input", { bubbles: true }));
  });
  await expect(student.locator('.work-textarea[data-qkey="work_0"]')).toHaveValue(/2\+2=4/);

  if (aiEnabled && (await student.locator('.type-step-check[data-qindex="0"]').count()) > 0) {
    const graded = student.waitForResponse(async r => {
      if (!r.url().includes("/Student/GradeProgress/")) return false;
      return (await r.json().catch(() => ({ status: "running" }))).status !== "running";
    }, { timeout: 120_000 });
    await student.locator('.type-step-check[data-qindex="0"]').click();
    const outcome = await (await graded).json() as { status: string; error?: string };
    expect(outcome.status, `step check reported: ${outcome.error ?? ""}`).toBe("done");
  }
});

test("photo and typed solution endpoints refuse bad requests without calling the AI", async () => {
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  const token = await student.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const post = async (path: string, form: Record<string, string>) =>
    (await student.request.post(path, { form: { ...form, __RequestVerificationToken: token } })).json();

  const typed = await post("/Student/SubmitTypedSteps", {
    assignmentId: "00000000-0000-4000-8000-000000000001", questionIndex: "0", stepsJson: "[]"
  });
  expect(typed.success, JSON.stringify(typed)).toBe(false);

  const photo = await post("/Student/SubmitHandwritten", {
    assignmentId, questionIndex: "0", imagePathsJson: JSON.stringify(["/uploads/submissions/someone-else/x.png"]), stepsJson: "[]"
  });
  expect(photo.success, JSON.stringify(photo)).toBe(false);
});

test("student answers and submits; the result page loads", async () => {
  test.setTimeout(180_000);
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);

  await student.locator('.nav-btn[data-idx="0"]').click();
  await student.locator('.answer-textarea[data-qkey="q_0"]').fill("4");
  await student.locator('.nav-btn[data-idx="1"]').click();
  await student.locator('.answer-textarea[data-qkey="q_1"]').fill("Because it is the sum of two twos.");

  await submitAssignment(student);
  await expect(student).toHaveURL(/\/Student\/AssignmentResult/i);
  resultId = student.url().match(/resultId=([0-9a-f-]+)/i)![1];
  const response = await student.reload();
  expect(response?.status()).toBeLessThan(400);
});

test("teacher grades the open-text answer by hand and the student sees the grade and comment", async () => {
  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${resultId}`);
  // Open-text answers are typed into a math field, so the stored text loses its spaces ("$Becauseitis…$").
  await expect(teacher.locator("body")).toContainText(/Because\s*it\s*is\s*the\s*sum\s*of\s*two\s*twos/);

  await teacher.locator('label[for="gcCorrect_1"]').click();
  await teacher.locator(".grade-points").nth(1).fill("1");
  await teacher.locator(".grade-comment").nth(1).fill(comment);
  await teacher.locator('#gradeForm button[type="submit"]').first().click();
  await expect(teacher).not.toHaveURL(/SubmissionDetail.*error/i);

  await student.goto(`/Student/AssignmentResult?assignmentId=${assignmentId}&resultId=${resultId}`);
  await expect(student.locator("body")).toContainText(comment);

  // The saved grade survives a reload on the teacher side as well.
  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${resultId}`);
  await expect(teacher.locator(".grade-comment").nth(1)).toHaveValue(comment);
  await expect(teacher.locator("#gcCorrect_1")).toBeChecked();
});
