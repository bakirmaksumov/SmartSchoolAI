// Import Cambridge Paper wizard: waits for a background reading, crops each question's figure out of the paper PDF
// (in the browser, once — the image is then stored and served like any other), lets the teacher change a part's
// syllabus topic with the same picker as the question form, and keeps the "Confirm N selected" count in step with
// the ticked rows. Everything here is progressive: without JavaScript the forms still post.
(function () {
  "use strict";
  const token = () => document.querySelector('input[name="__RequestVerificationToken"]')?.value || "";

  // ── Waiting for a reading ──────────────────────────────────────────────────────────────────────────────────────
  const poll = document.querySelector("[data-poll]");
  if (poll) {
    const which = poll.dataset.poll;            // "paper" | "markScheme"
    const url = `/CambridgeImport/${poll.dataset.import}/Status`;
    const tick = async () => {
      try {
        const r = await fetch(url, { headers: { Accept: "application/json" }, cache: "no-store" });
        if (r.ok) {
          const s = await r.json();
          if (s[which] !== "Running" && s[which] !== "NotStarted") { location.reload(); return; }
        }
      } catch { /* offline for a moment: keep waiting */ }
      setTimeout(tick, 2000);
    };
    setTimeout(tick, 1500);
  }

  // ── Figures: crop from the paper once, then store ──────────────────────────────────────────────────────────────
  const crops = [...document.querySelectorAll("canvas[data-crop]")];
  if (crops.length && window.pdfjsLib) {
    pdfjsLib.GlobalWorkerOptions.workerSrc = "https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/build/pdf.worker.min.js";
    const doc = pdfjsLib.getDocument({ url: `/CambridgeImport/${crops[0].dataset.import}/File/paper`, withCredentials: true }).promise;
    crops.forEach(async canvas => {
      try {
        const { page: pageNo, box } = JSON.parse(canvas.dataset.box);
        const page = await (await doc).getPage(pageNo);
        const viewport = page.getViewport({ scale: 2.5 });
        const full = document.createElement("canvas");
        full.width = viewport.width; full.height = viewport.height;
        await page.render({ canvasContext: full.getContext("2d"), viewport }).promise;
        const [l, t, r, b] = box.map(v => Math.min(1, Math.max(0, Number(v))));
        const x = Math.floor(l * full.width), y = Math.floor(t * full.height);
        const w = Math.max(20, Math.ceil((r - l) * full.width)), h = Math.max(20, Math.ceil((b - t) * full.height));
        canvas.width = w; canvas.height = h;
        const ctx = canvas.getContext("2d");
        ctx.fillStyle = "#fff"; ctx.fillRect(0, 0, w, h);
        ctx.drawImage(full, x, y, w, h, 0, 0, w, h);
        const blob = await new Promise(res => canvas.toBlob(res, "image/png"));
        const fd = new FormData();
        fd.append("image", blob, `${canvas.dataset.key}.png`);
        fd.append("__RequestVerificationToken", token());
        await fetch(`/CambridgeImport/${canvas.dataset.import}/Figure/${canvas.dataset.key}`, { method: "POST", body: fd });
        canvas.dataset.stored = "1";
      } catch (e) {
        canvas.replaceWith(Object.assign(document.createElement("div"), {
          className: "small text-warning", textContent: "The figure could not be cut from the PDF; open the paper page to see it."
        }));
      }
    });
  }

  // ── Topic: the question form's picker, opened on demand; choosing a topic saves it ─────────────────────────────
  document.querySelectorAll("[data-topic-form]").forEach(form => {
    const btn = form.querySelector("[data-change-topic]");
    const slot = form.querySelector("[data-picker-slot]");
    const root = form.querySelector("[data-syllabus-picker-lazy]");
    if (!btn || !slot || !root || !window.SyllabusPicker) return;
    const initial = root.dataset.value || "";
    btn.addEventListener("click", async () => {
      slot.hidden = false;
      btn.hidden = true;
      const picker = window.SyllabusPicker.mount(root);
      await picker.ready;
      root.addEventListener("syllabus-change", e => {
        if ((e.detail.topicId || "") !== initial) form.submit();
      });
      (root.querySelector("[data-sp-change]:not([hidden])") || root.querySelector("[data-sp-open]"))?.click();
      const field = root.closest("[data-sp-field]"); if (field) field.hidden = false;
    });
  });

  // ── Step 3: "Confirm N selected" follows the ticked rows ───────────────────────────────────────────────────────
  const bulk = document.getElementById("confirmSelected");
  if (bulk) {
    const boxes = [...document.querySelectorAll("input[data-bulk]")];
    const update = () => {
      const n = boxes.filter(b => b.checked).length;
      bulk.disabled = n === 0;
      bulk.querySelector("[data-count]").textContent = n;
    };
    boxes.forEach(b => b.addEventListener("change", update));
    document.getElementById("selectAllMatched")?.addEventListener("change", e => { boxes.forEach(b => b.checked = e.target.checked); update(); });
    update();
  }

  // Back to the list on a phone: return to the row the teacher came from.
  const selected = document.querySelector("tr.is-selected");
  if (selected && window.matchMedia("(min-width: 992px)").matches) selected.scrollIntoView({ block: "nearest" });
  // After the page (and the theme's own scripts) have settled, so nothing takes the focus back.
  if (location.hash.startsWith("#row-"))
    window.addEventListener("load", () => setTimeout(() => {
      const link = document.getElementById(location.hash.slice(1))?.querySelector("a");
      link?.focus();
      link?.scrollIntoView({ block: "center" });
    }, 50));

  // Saving: one press only (a second click would post the same parts again; the server ignores it anyway).
  document.querySelectorAll("form[data-once]").forEach(f => f.addEventListener("submit", () => {
    f.querySelectorAll("button[type=submit]").forEach(b => { b.disabled = true; });
  }));
})();
