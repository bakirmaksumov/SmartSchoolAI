// A deterministic stand-in for the OpenAI-compatible API the app talks to (OpenRouter).
//
// The application is pointed at it with OpenRouter__BaseUrl, so tutor chat, OCR and grading
// run through the real controllers, services and browser code; only the model's answer is
// scripted. Tests control it over a small HTTP API and can read back what the app sent.
//
//   POST /chat/completions   the OpenAI-compatible endpoint the app calls
//   POST /__script           set replies and delays (see `defaults` below for the fields)
//   POST /__reset            clear scripted replies and recorded requests
//   GET  /__requests?kind=   what the app sent: [{ kind, system, user, stream }]
//   GET  /health             readiness probe
//
// Request kinds: tutor (streamed), ocr, eval (step grading), verify (second opinion),
// answer (typed-answer / choice grading), other.
//
// Environment: E2E_MOCK_AI_PORT (default 5199), E2E_MOCK_AI_HOST (default 127.0.0.1; CI uses 0.0.0.0
// so a container can reach it through host.docker.internal).
import { createServer } from "node:http";

const port = Number(process.env.E2E_MOCK_AI_PORT ?? 5199);
const host = process.env.E2E_MOCK_AI_HOST ?? "127.0.0.1";

const defaults = () => ({
  tutorChunks: ["Here is a hint."],
  tutorDelayMs: 15,
  ocrDelayMs: 0,    // hold the OCR answer back, to leave time to look at the waiting screen
  evalDelayMs: 0,   // the same for the step-grading answer
  ocr: { detectedProblem: "", steps: [{ number: 1, text: "$x = 1$" }] },
  // Scripted model answers. When unset the mock behaves as an agreeable grader: every step "correct".
  evalReply: null,    // { steps: [{ number, text?, status, explanation }], overall, expectedAnswer, summary }
  verifyReply: null,  // { overall, agrees, confidence, notes, steps?: [{ number, status, explanation }] }
  answerReply: null   // { correct, feedback }
});

let script = defaults();
let requests = [];

const readBody = req => new Promise((resolve, reject) => {
  const parts = [];
  req.on("data", chunk => parts.push(chunk));
  req.on("end", () => resolve(Buffer.concat(parts).toString("utf8")));
  req.on("error", reject);
});

const json = (res, status, value) => {
  res.writeHead(status, { "Content-Type": "application/json" });
  res.end(JSON.stringify(value));
};

const completion = content => ({ choices: [{ message: { role: "assistant", content } }] });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function describe(body) {
  const messages = body.messages ?? [];
  const system = messages.filter(m => m.role === "system").map(m => String(m.content)).join("\n");
  const last = messages[messages.length - 1] ?? {};
  const hasImage = Array.isArray(last.content) && last.content.some(part => part.type === "image_url");
  const user = Array.isArray(last.content)
    ? last.content.filter(part => part.type === "text").map(part => part.text).join("\n")
    : String(last.content ?? "");

  let kind = "other";
  if (body.stream === true) kind = "tutor";
  else if (hasImage && system.includes("OCR engine")) kind = "ocr";
  else if (system.includes("expert math teacher")) kind = "eval";
  else if (system.includes("exam moderator")) kind = "verify";
  else if (system.includes("grading students' typed answers")) kind = "answer";
  return { kind, system, user, stream: body.stream === true };
}

// "Student's steps:\n1. text\n2. text" -> [{ number, text }]
function parseSteps(user) {
  const block = user.split("Student's steps:")[1] ?? "";
  return block.split("\n")
    .map(line => /^(\d+)\.\s(.*)$/.exec(line))
    .filter(Boolean)
    .map(m => ({ number: Number(m[1]), text: m[2] }));
}

async function chatCompletions(req, res) {
  const body = JSON.parse(await readBody(req) || "{}");
  const seen = describe(body);
  requests.push(seen);

  if (seen.kind === "tutor") {
    res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" });
    for (const piece of script.tutorChunks) {
      res.write(`data: ${JSON.stringify({ choices: [{ delta: { content: piece } }] })}\n\n`);
      await sleep(script.tutorDelayMs);
    }
    res.write("data: [DONE]\n\n");
    res.end();
    return;
  }

  if (seen.kind === "ocr") {
    await sleep(script.ocrDelayMs);
    return json(res, 200, completion(JSON.stringify(script.ocr)));
  }

  if (seen.kind === "eval") {
    await sleep(script.evalDelayMs);
    if (script.evalReply) return json(res, 200, completion(JSON.stringify(script.evalReply)));
    const steps = parseSteps(seen.user).map(s => ({ ...s, status: "correct", explanation: "" }));
    return json(res, 200, completion(JSON.stringify({
      steps, overall: "correct", expectedAnswer: "x = 5/2 or x = 1/2", summary: "All steps are correct."
    })));
  }

  if (seen.kind === "verify") {
    const reply = script.verifyReply ?? { overall: "correct", agrees: true, confidence: 0.95, notes: "Agreed." };
    return json(res, 200, completion(JSON.stringify(reply)));
  }

  if (seen.kind === "answer") {
    // "Item 2:" -> the question index the app is asking about.
    const index = Number(/Item (\d+):/.exec(seen.user)?.[1] ?? 0);
    const reply = script.answerReply ?? { correct: true, feedback: "That is right." };
    return json(res, 200, completion(JSON.stringify([{ index, ...reply }])));
  }

  return json(res, 200, completion("{}"));
}

createServer(async (req, res) => {
  try {
    const url = new URL(req.url ?? "/", "http://localhost");
    if (req.method === "GET" && url.pathname === "/health") return json(res, 200, { status: "ok" });
    if (req.method === "POST" && url.pathname === "/chat/completions") return await chatCompletions(req, res);

    if (req.method === "POST" && url.pathname === "/__reset") {
      script = defaults();
      requests = [];
      return json(res, 200, { ok: true });
    }
    if (req.method === "POST" && url.pathname === "/__script") {
      script = { ...script, ...JSON.parse(await readBody(req) || "{}") };
      return json(res, 200, { ok: true });
    }
    if (req.method === "GET" && url.pathname === "/__requests") {
      const kind = url.searchParams.get("kind");
      return json(res, 200, kind ? requests.filter(r => r.kind === kind) : requests);
    }
    json(res, 404, { error: "not found" });
  } catch (error) {
    json(res, 500, { error: String(error) });
  }
}).listen(port, host, () => console.log(`mock AI listening on http://${host}:${port}`));
