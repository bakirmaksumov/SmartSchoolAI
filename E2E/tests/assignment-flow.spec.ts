import { expect, test } from "@playwright/test";
import {
  addQuestion,
  createClass,
  createDraftAssignment,
  joinClass,
  publishAndAssignAll,
  signInAs,
  submitAssignment,
  unique,
  type QuestionSpec
} from "./helpers";

interface PlannedQuestion extends QuestionSpec {
  // What the student submits; equals answerKey when the answer is correct.
  studentAnswer: string;
}

function buildQuestions(): PlannedQuestion[] {
  const questions: PlannedQuestion[] = [];

  for (let n = 1; n <= 4; n++) {
    questions.push({
      kind: "Text/Numeric",
      text: `Numeric ${n}: what is ${n} times 7?`,
      answerKey: String(n * 7),
      studentAnswer: n === 1 ? "-1" : String(n * 7)
    });
  }

  const letters = ["A", "B", "C"];
  for (let n = 1; n <= 3; n++) {
    questions.push({
      kind: "Single choice",
      text: `Single ${n}: pick the correct option`,
      options: ["Alpha", "Beta", "Gamma"],
      answerKey: letters[n - 1],
      studentAnswer: n === 1 ? "B" : letters[n - 1]
    });
  }

  for (let n = 1; n <= 3; n++) {
    questions.push({
      kind: "Multiple choice",
      text: `Multiple ${n}: pick all correct options`,
      options: ["Red", "Green", "Blue", "Yellow"],
      answerKey: "A,C",
      studentAnswer: n === 1 ? "A" : "A,C"
    });
  }

  return questions;
}

test.describe.configure({ mode: "serial" });

test("teacher builds a 10-question mixed assignment, student answers it, scores match everywhere", async ({ browser }) => {
  test.setTimeout(600_000);

  const questions = buildQuestions();
  const wrongCount = questions.filter(q => q.studentAnswer !== q.answerKey).length;
  const expectedScore = ((questions.length - wrongCount) / questions.length) * 100;

  const teacherPage = await browser.newPage();
  const studentPage = await browser.newPage();
  const className = unique("E2E Mixed Class");
  const assignmentTitle = unique("E2E Mixed Assignment");

  try {
    await signInAs(teacherPage, "teacher");
    await expect(teacherPage.getByRole("heading", { name: "Dashboard" })).toBeVisible();
    const inviteCode = await createClass(teacherPage, className);

    await signInAs(studentPage, "student");
    await expect(studentPage.getByRole("heading", { name: /Welcome to EduSchool AI/ })).toBeVisible();
    await joinClass(studentPage, inviteCode, className);

    const assignmentId = await createDraftAssignment(teacherPage, assignmentTitle, className);
    for (const question of questions) {
      await addQuestion(teacherPage, question);
    }
    await expect(teacherPage.locator("#questionsList")).toContainText(questions[0].text);

    await publishAndAssignAll(teacherPage, assignmentId);

    await studentPage.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
    for (let i = 0; i < questions.length; i++) {
      const q = questions[i];
      await studentPage.locator(`.nav-btn[data-idx="${i}"]`).click();
      await expect(studentPage.getByText(q.text, { exact: true })).toBeVisible();

      if (q.kind === "Text/Numeric") {
        await studentPage.locator(`.answer-textarea[data-qkey="q_${i}"]`).fill(q.studentAnswer);
      } else {
        for (const letter of q.studentAnswer.split(",")) {
          await studentPage.locator(`.option-card[data-qkey="q_${i}"][data-value="${letter}"]`).click();
        }
      }
    }

    await submitAssignment(studentPage);
    await expect(studentPage.getByText(`${expectedScore}%`, { exact: true }).first()).toBeVisible();

    await teacherPage.goto(`/Assignments/Details/${assignmentId}`);
    const resultRow = teacherPage.getByRole("row").filter({ hasText: "E2E Student" });
    await expect(resultRow.getByText(`${expectedScore}%`, { exact: true })).toBeVisible();
  } finally {
    await Promise.all([teacherPage.close(), studentPage.close()]);
  }
});

test("student who answers every question correctly gets 100%", async ({ browser }) => {
  test.setTimeout(300_000);

  const teacherPage = await browser.newPage();
  const studentPage = await browser.newPage();
  const className = unique("E2E Perfect Class");

  try {
    await signInAs(teacherPage, "teacher");
    const inviteCode = await createClass(teacherPage, className);
    await signInAs(studentPage, "student");
    await joinClass(studentPage, inviteCode, className);

    const assignmentId = await createDraftAssignment(teacherPage, unique("E2E Perfect"), className);
    for (let n = 1; n <= 5; n++) {
      await addQuestion(teacherPage, {
        kind: "Text/Numeric",
        text: `Perfect ${n}: ${n} plus ${n}`,
        answerKey: String(n * 2)
      });
    }
    await publishAndAssignAll(teacherPage, assignmentId);

    await studentPage.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
    for (let i = 0; i < 5; i++) {
      await studentPage.locator(`.nav-btn[data-idx="${i}"]`).click();
      await studentPage.locator(`.answer-textarea[data-qkey="q_${i}"]`).fill(String((i + 1) * 2));
    }
    await submitAssignment(studentPage);
    await expect(studentPage.getByText("100%", { exact: true }).first()).toBeVisible();
  } finally {
    await Promise.all([teacherPage.close(), studentPage.close()]);
  }
});
