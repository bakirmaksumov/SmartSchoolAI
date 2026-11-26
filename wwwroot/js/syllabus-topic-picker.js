// Optional "Link to Cambridge 9709" on a question form: finds a published syllabus topic by part of its name,
// optionally within one component, and stores its id in a hidden input. The link is the teacher's explicit choice;
// it never replaces the question's own subject/topic text. Works with the keyboard (arrows, Enter, Escape) and on
// phones. The topic list is the local published catalogue (/Syllabus/Catalog) — no AI is involved.
(function () {
  "use strict";
  let catalogPromise = null;
  function catalog() {
    if (!catalogPromise) {
      catalogPromise = fetch("/Syllabus/Catalog", { credentials: "same-origin", headers: { Accept: "application/json" } })
        .then(r => (r.ok ? r.json() : { versions: [] }))
        .then(d => d.versions || [])
        .catch(() => []);
    }
    return catalogPromise;
  }

  function findTopic(versions, id) {
    for (const v of versions)
      for (const c of v.components)
        for (const t of c.topics)
          if (t.id === id) return { v, c, t };
    return null;
  }

  const label = hit => `${hit.v.code} · ${hit.v.years} · ${hit.c.title} · ${hit.t.title}`;
  let uid = 0;

  function mount(root) {
    if (root.__syllabusPicker) return root.__syllabusPicker;
    const id = "sp" + (++uid);
    const name = root.dataset.name || "syllabusTopicId";
    root.innerHTML = `
      <input type="hidden" name="${name}" data-sp-value>
      <button type="button" class="btn btn-sm btn-outline-primary" data-sp-open hidden>
        <i class="bx bx-link me-1" aria-hidden="true"></i><span data-sp-open-text>Link to Cambridge syllabus</span>
      </button>
      <div class="sp-chosen" data-sp-chosen hidden>
        <span class="badge bg-label-info mb-1" data-sp-aligned>Aligned to Cambridge</span>
        <div class="small fw-semibold text-break" data-sp-label></div>
        <div class="d-flex gap-2 mt-1">
          <button type="button" class="btn btn-sm btn-link p-0" data-sp-change>Change</button>
          <button type="button" class="btn btn-sm btn-link p-0 text-danger" data-sp-remove>Remove link</button>
        </div>
      </div>
      <div class="sp-panel border rounded p-2 mt-1" data-sp-panel hidden>
        <div class="mb-2" data-sp-version-wrap hidden>
          <label class="form-label small mb-1" for="${id}-version">Syllabus version</label>
          <select class="form-select form-select-sm" id="${id}-version" data-sp-version></select>
        </div>
        <div class="mb-2">
          <label class="form-label small mb-1" for="${id}-component">Component</label>
          <select class="form-select form-select-sm" id="${id}-component" data-sp-component></select>
        </div>
        <label class="form-label small mb-1" for="${id}-search">Topic</label>
        <input type="text" class="form-control form-control-sm" id="${id}-search" data-sp-search autocomplete="off"
               role="combobox" aria-autocomplete="list" aria-expanded="true" aria-controls="${id}-list"
               placeholder="Type part of a topic, e.g. quad">
        <ul class="list-unstyled sp-list mb-0 mt-1" id="${id}-list" role="listbox" aria-label="Syllabus topics" data-sp-list></ul>
        <div class="small text-muted mt-1" data-sp-empty hidden role="status"></div>
        <div class="text-end mt-1"><button type="button" class="btn btn-sm btn-outline-secondary" data-sp-cancel>Cancel</button></div>
      </div>`;
    const $ = s => root.querySelector(s);
    const hidden = $("[data-sp-value]"), openBtn = $("[data-sp-open]"), chosen = $("[data-sp-chosen]"),
          panel = $("[data-sp-panel]"), versionSel = $("[data-sp-version]"), compSel = $("[data-sp-component]"),
          search = $("[data-sp-search]"), list = $("[data-sp-list]"), empty = $("[data-sp-empty]");
    let versions = [], active = -1, results = [];

    function version() { return versions.find(v => v.id === versionSel.value) || versions[0]; }

    function fillComponents() {
      const v = version();
      compSel.innerHTML = `<option value="">All components</option>` +
        (v ? v.components.map(c => `<option value="${c.id}">${c.number} · ${escapeHtml(c.title)}</option>`).join("") : "");
    }

    function render() {
      const v = version();
      const q = search.value.trim().toLowerCase();
      results = [];
      if (v) for (const c of v.components) {
        if (compSel.value && c.id !== compSel.value) continue;
        for (const t of c.topics)
          if (!q || t.title.toLowerCase().includes(q) || (t.number || "").startsWith(q)) results.push({ v, c, t });
      }
      active = results.length ? 0 : -1;
      list.innerHTML = results.map((r, i) =>
        `<li role="option" id="${id}-opt-${i}" class="sp-option" data-index="${i}" aria-selected="${i === active}">
           <span class="fw-semibold">${escapeHtml(r.t.title)}</span> <span class="text-muted">· ${escapeHtml(r.c.title)}</span>
         </li>`).join("");
      empty.hidden = results.length > 0;
      empty.textContent = results.length ? "" :
        (compSel.value ? "No topic matches in this component. Change the search or choose All components."
                       : "No topic matches. Try a shorter part of the name.");
      highlight();
    }

    function highlight() {
      list.querySelectorAll(".sp-option").forEach((li, i) => {
        li.classList.toggle("active", i === active);
        li.setAttribute("aria-selected", String(i === active));
      });
      if (active >= 0) {
        search.setAttribute("aria-activedescendant", `${id}-opt-${active}`);
        list.querySelector(`#${id}-opt-${active}`)?.scrollIntoView({ block: "nearest" });
      } else search.removeAttribute("aria-activedescendant");
    }

    function show(stateId) {
      const hit = stateId ? findTopic(versions, stateId) : null;
      hidden.value = stateId || "";
      chosen.hidden = !stateId;
      openBtn.hidden = !!stateId || versions.length === 0;
      panel.hidden = true;
      if (stateId) $("[data-sp-label]").textContent = hit ? label(hit) : "Linked syllabus topic";
      root.dispatchEvent(new CustomEvent("syllabus-change", { bubbles: true, detail: { topicId: hidden.value } }));
    }

    function open() {
      const current = findTopic(versions, hidden.value);
      if (current) versionSel.value = current.v.id;
      fillComponents();
      compSel.value = current ? current.c.id : "";
      search.value = "";
      panel.hidden = false;
      openBtn.hidden = true;
      chosen.hidden = true;
      render();
      search.focus();
    }

    function choose(i) {
      const r = results[i];
      if (!r) return;
      show(r.t.id);
      $("[data-sp-change]").focus();
    }

    function cancel() {
      show(hidden.value);
      (hidden.value ? $("[data-sp-change]") : openBtn).focus();
    }

    openBtn.addEventListener("click", open);
    $("[data-sp-change]").addEventListener("click", open);
    $("[data-sp-remove]").addEventListener("click", () => { show(""); openBtn.focus(); });
    $("[data-sp-cancel]").addEventListener("click", cancel);
    versionSel.addEventListener("change", () => { fillComponents(); render(); });
    compSel.addEventListener("change", render);
    search.addEventListener("input", render);
    search.addEventListener("keydown", e => {
      if (e.key === "ArrowDown") { e.preventDefault(); if (results.length) { active = (active + 1) % results.length; highlight(); } }
      else if (e.key === "ArrowUp") { e.preventDefault(); if (results.length) { active = (active - 1 + results.length) % results.length; highlight(); } }
      else if (e.key === "Enter") { e.preventDefault(); choose(active); }   // never submits the question form
      else if (e.key === "Escape") { e.preventDefault(); cancel(); }
    });
    list.addEventListener("click", e => {
      const li = e.target.closest(".sp-option");
      if (li) choose(Number(li.dataset.index));
    });

    const api = {
      ready: catalog().then(vs => {
        versions = vs;
        const field = root.closest("[data-sp-field]");
        if (field) field.hidden = vs.length === 0 && !(root.dataset.value || hidden.value);
        versionSel.innerHTML = vs.map(v => `<option value="${v.id}">${escapeHtml(v.label)}</option>`).join("");
        $("[data-sp-version-wrap]").hidden = vs.length < 2;   // one published version is chosen automatically
        const code = vs[0]?.code || "";
        $("[data-sp-open-text]").textContent = code ? `Link to Cambridge ${code}` : "Link to Cambridge syllabus";
        $("[data-sp-aligned]").textContent = code ? `Aligned to Cambridge ${code}` : "Aligned to Cambridge";
        show(root.dataset.value || hidden.value || "");
      }),
      set(topicId) { return api.ready.then(() => show(topicId || "")); },
      get() { return hidden.value || null; },
    };
    root.__syllabusPicker = api;
    return api;
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[ch]));
  }

  window.SyllabusPicker = { mount, catalog };
  document.addEventListener("DOMContentLoaded", () =>
    document.querySelectorAll("[data-syllabus-picker]").forEach(mount));
})();
