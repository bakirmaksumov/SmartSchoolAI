/* ═══════════════════════════════════════════════════════════════════════════
   type-steps.js – Typed step-by-step solution editor + per-step checking, one
   instance per question ("Type solution" tab of the "Show your work" panel).

   Sibling to handwriting.js (same numbered-row markup/classes, same
   check → render-results shape) but for typed text instead of a photo: no
   OCR pass, steps go straight to POST /Student/SubmitTypedSteps.

   Each step is an ordinary text field: the student writes words and maths together
   ("Stretch by factor 2 parallel to the y-axis", "x = 2.5 or x = 1/2") and it is stored and
   sent exactly as typed, spaces included, with no "$…$" wrapping. Steps saved by an older
   version (LaTeX in "$…$") are shown as readable text through MathText, and are sent back
   unchanged while the student does not edit them, so their meaning is kept.

   Not a single shared instance like the hw modal — every question panel has
   its own step list, so everything here is scoped by data-qindex rather than
   by one global piece of state.
   ═══════════════════════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  document.addEventListener('DOMContentLoaded', function () {
    var lists = document.querySelectorAll('.type-steps-list');
    if (lists.length === 0) return;

    lists.forEach(initStepList);
    bindCheckButtons();
    bindReportModal();
  });

  // A step's text as it is stored and submitted. Typed text goes out as typed; a step restored
  // from an older "$…$" save goes out as that original while the field still shows the readable
  // form it was seeded with (so converting for display never changes what is graded).
  function rowValue(row) {
    var text = row.querySelector('.type-step-input').value.trim();
    if (text && row.dataset.display !== undefined && text === row.dataset.display) {
      return row.dataset.original;
    }
    return text;
  }

  // ── Init one question's step list ─────────────────────────────────────────
  function initStepList(list) {
    var qIndex = list.dataset.qindex;

    // A page reload may have already restored the joined text into the hidden
    // mirror textarea (take-assignment.js's hydrateAnswers, which runs before
    // this script) — split it back into rows instead of starting blank.
    var mirror = document.querySelector('.work-textarea[data-qkey="work_' + qIndex + '"]');
    var restored = mirror && mirror.value ? mirror.value.split('\n') : [];

    if (restored.length > 0) {
      restored.forEach(function (text, i) { addStepRow(list, i + 1, text); });
    } else {
      addStepRow(list, 1, '');
    }

    list.addEventListener('input', function (e) {
      if (e.target.classList && e.target.classList.contains('type-step-input')) {
        syncMirror(qIndex);
      }
    });

    var addBtn = document.querySelector('.type-step-add[data-qindex="' + qIndex + '"]');
    if (addBtn) {
      addBtn.addEventListener('click', function () {
        var row = addStepRow(list, stepCount(list) + 1, '');
        var field = row.querySelector('.type-step-input');
        if (field && typeof field.focus === 'function') field.focus();
        syncMirror(qIndex);
      });
    }
  }

  function addStepRow(list, number, stored) {
    var row = document.createElement('div');
    row.className = 'hw-step-row';
    row.innerHTML =
      '<span class="hw-step-num"></span>' +
      '<input type="text" class="form-control type-step-input" autocomplete="off" spellcheck="false" />' +
      '<button type="button" class="btn btn-sm btn-outline-danger type-step-del"><i class="bx bx-trash"></i></button>';
    row.querySelector('.hw-step-num').textContent = number;
    row.querySelector('.type-step-input').setAttribute('aria-label', 'Step ' + number);
    seedStep(row, stored || '');

    row.querySelector('.type-step-del').addEventListener('click', function () {
      var qIndex = list.dataset.qindex;
      row.remove();
      if (stepCount(list) === 0) addStepRow(list, 1, '');   // never leave zero rows
      renumberSteps(list);
      syncMirror(qIndex);
    });

    list.appendChild(row);
    return row;
  }

  // Puts a stored step into its field: simple LaTeX from an older save is shown readably,
  // anything that cannot be converted reliably is kept as written with a formatted preview.
  function seedStep(row, stored) {
    var input = row.querySelector('.type-step-input');
    var shown = window.MathText ? MathText.inline(stored) : { text: stored, hasComplex: false };
    row.dataset.original = stored;

    if (!shown.hasComplex) {
      input.value = shown.text;
      row.dataset.display = shown.text;
      return;
    }

    input.value = stored;
    var note = document.createElement('div');
    note.className = 'hw-step-preview';
    row.appendChild(note);
    var refresh = function () {
      note.textContent = '$' + input.value.replace(/^\$+|\$+$/g, '') + '$';
      if (typeof renderMathInElement === 'function') {
        renderMathInElement(note, { delimiters: [{ left: '$', right: '$', display: false }], throwOnError: false });
      }
    };
    refresh();
    input.addEventListener('input', refresh);
  }

  function renumberSteps(list) {
    list.querySelectorAll('.hw-step-row').forEach(function (row, i) {
      row.querySelector('.hw-step-num').textContent = i + 1;
      row.querySelector('.type-step-input').setAttribute('aria-label', 'Step ' + (i + 1));
    });
  }

  function stepCount(list) {
    return list.querySelectorAll('.hw-step-row').length;
  }

  function collectSteps(list) {
    var steps = [];
    list.querySelectorAll('.hw-step-row').forEach(function (row, i) {
      var text = rowValue(row);
      if (text) steps.push({ number: i + 1, text: text });
    });
    return steps;
  }

  // Mirrors the steps into the hidden work_i textarea as joined lines (one per step), then fires a real "input" event so take-assignment.js's own
  // listener (already bound to .work-textarea) persists it exactly like a
  // keystroke would — answers{}, the hidden POST field, the session draft,
  // all for free.
  function syncMirror(qIndex) {
    var list = document.querySelector('.type-steps-list[data-qindex="' + qIndex + '"]');
    var mirror = document.querySelector('.work-textarea[data-qkey="work_' + qIndex + '"]');
    if (!list || !mirror) return;

    var lines = [];
    list.querySelectorAll('.hw-step-row').forEach(function (row) {
      lines.push(rowValue(row));
    });
    mirror.value = lines.join('\n');
    mirror.dispatchEvent(new Event('input', { bubbles: true }));
  }

  // ── Check my work ────────────────────────────────────────────────────────
  function bindCheckButtons() {
    document.querySelectorAll('.type-step-check').forEach(function (btn) {
      btn.addEventListener('click', function () { checkWork(btn); });
    });
  }

  async function checkWork(btn) {
    var qIndex = btn.dataset.qindex;
    var list = document.querySelector('.type-steps-list[data-qindex="' + qIndex + '"]');
    var resultsBox = document.querySelector('.type-step-results[data-qindex="' + qIndex + '"]');
    var steps = collectSteps(list);

    if (steps.length === 0) {
      renderError(resultsBox, 'Write your working first — at least one step.');
      return;
    }

    var original = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Checking…';
    if (resultsBox) resultsBox.innerHTML = '';

    var restoreBtn = function () { btn.disabled = false; btn.innerHTML = original; };

    var applyResult = function (result) {
      renderResults(resultsBox, result);
      markPillResult(qIndex, (result && (result.overall || '')).toLowerCase() === 'correct');
      restoreBtn();
    };

    try {
      var fd = new FormData();
      fd.append('assignmentId', document.getElementById('assignmentId').value);
      fd.append('questionIndex', qIndex);
      fd.append('stepsJson', JSON.stringify(steps));
      fd.append('__RequestVerificationToken', token());

      var res = await fetch('/Student/SubmitTypedSteps', { method: 'POST', body: fd });
      var data = await res.json();
      if (!data.success) throw new Error(data.error || 'Evaluation failed.');

      // Grading runs in the background (grade + a second-opinion pass). Poll for it,
      // and — since this flow has no loading screen — play a game in the meantime.
      if (window.WaitingGame && window.GradePoll) {
        WaitingGame.start();
        GradePoll.start(data.jobId, {
          onDone: function (result) { WaitingGame.finish(function () { applyResult(result); }); },
          onError: function (msg)   { WaitingGame.close(); renderError(resultsBox, msg); restoreBtn(); }
        });
      } else if (window.GradePoll) {
        GradePoll.start(data.jobId, {
          onDone: applyResult,
          onError: function (msg) { renderError(resultsBox, msg); restoreBtn(); }
        });
      } else {
        renderError(resultsBox, 'Could not check your work. Please try again.');
        restoreBtn();
      }
    } catch (err) {
      if (window.WaitingGame) WaitingGame.close();
      renderError(resultsBox, err.message);
      restoreBtn();
    }
  }

  // Same card shape as handwriting.js's renderResults — same classes, so the
  // two checking paths (typed vs photo) read as one visual language.
  function renderResults(wrap, data) {
    if (!wrap) return;
    var correct = (data.overall || '').toLowerCase() === 'correct';

    // Just this question's verdict — data.score is the whole assignment's running
    // percentage (recomputed after every question), not this one's, so it's left
    // out here; showing it next to a single question's result read as if it were
    // that question's own score.
    var html =
      '<div class="alert alert-' + (correct ? 'success' : 'warning') + ' py-2 px-3">' +
        '<strong>' + (correct ? '✅ Correct' : '❌ Not correct') + '</strong>' +
        (data.summary ? '<div class="small mt-1">' + escapeHtml(readable(data.summary)) + '</div>' : '') +
      '</div>';

    var firstMistake = true;
    (data.steps || []).forEach(function (s, i) {
      var ok = (s.status || '').toLowerCase() === 'correct';
      // The reason sits directly under the step as ordinary text, so it reads on a phone
      // (a hover tooltip never does). The first wrong step is marked as where to start.
      var badge = (!ok && firstMistake) ? ' <span class="badge bg-label-danger hw-first-mistake">First mistake</span>' : '';
      if (!ok) firstMistake = false;
      html +=
        '<div class="hw-result-step ' + (ok ? 'correct' : 'incorrect') + '">' +
          '<div class="hw-step-text">' + (ok ? '✅' : '❌') + ' Step ' + s.number + ': ' + escapeHtml(readable(s.text)) + badge + '</div>' +
          (!ok && s.explanation ? '<div class="hw-step-reason">' + escapeHtml(readable(s.explanation)) + '</div>' : '') +
          (!ok ? '<button type="button" class="btn btn-sm btn-outline-primary mt-1 type-step-help" data-step-index="' + i + '">' +
                   '<i class="bx bx-help-circle me-1"></i>Get help</button>' : '') +
        '</div>';
    });

    wrap.innerHTML = html;

    // "Get help" opens the tutor about that very step, with the checker's reason.
    wrap.querySelectorAll('.type-step-help').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var step = (data.steps || [])[parseInt(btn.dataset.stepIndex, 10)];
        if (!step || !window.openTutorial) return;
        window.openTutorial({ step: { number: step.number, text: step.text, feedback: step.explanation || '' } });
      });
    });

    if (typeof renderMathInElement === 'function') {
      renderMathInElement(wrap, {
        delimiters: [
          { left: '$$', right: '$$', display: true },
          { left: '$',  right: '$',  display: false },
          { left: '\\(', right: '\\)', display: false },
          { left: '\\[', right: '\\]', display: true }
        ],
        throwOnError: false
      });
    }

    if (typeof bootstrap !== 'undefined') {
      wrap.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(function (el) {
        bootstrap.Tooltip.getOrCreateInstance(el);
      });
    }
  }

  // Plain text with simple maths made readable (complex notation is left as written).
  function readable(str) {
    return window.MathText ? MathText.inline(str).text : String(str || '');
  }

  function renderError(wrap, message) {
    if (!wrap) return;
    wrap.innerHTML = '<div class="alert alert-danger py-2 px-3 mb-0">' + escapeHtml(message) + '</div>';
  }

  // Colours the question's nav pill, matching how the flat-answer "Check
  // answer" button already marks it (see take-assignment.js markPillResult).
  function markPillResult(qIndex, ok) {
    var pill = document.querySelector('.question-nav .nav-btn[data-idx="' + qIndex + '"]');
    if (!pill) return;
    pill.classList.toggle('correct', ok);
    pill.classList.toggle('wrong', !ok);
    pill.classList.add('answered');
  }

  // ── Report a problem ─────────────────────────────────────────────────────
  function bindReportModal() {
    var modalEl = document.getElementById('reportProblemModal');
    if (!modalEl) return;   // preview mode has no modal
    var modal = window.bootstrap ? new bootstrap.Modal(modalEl) : null;

    document.querySelectorAll('.type-step-report').forEach(function (btn) {
      btn.addEventListener('click', function () {
        modalEl.dataset.qindex = btn.dataset.qindex;
        document.getElementById('reportProblemText').value = '';
        document.getElementById('reportProblemStatus').textContent = '';
        if (modal) modal.show();
      });
    });

    var sendBtn = document.getElementById('reportProblemSubmitBtn');
    if (sendBtn) {
      sendBtn.addEventListener('click', async function () {
        var qIndex = modalEl.dataset.qindex;
        var message = document.getElementById('reportProblemText').value.trim();
        var status = document.getElementById('reportProblemStatus');
        if (!message) { status.className = 'small mt-2 text-danger'; status.textContent = 'Please describe the problem.'; return; }

        sendBtn.disabled = true;
        try {
          var fd = new FormData();
          fd.append('assignmentId', document.getElementById('assignmentId').value);
          fd.append('questionIndex', qIndex);
          fd.append('message', message);
          fd.append('__RequestVerificationToken', token());

          var res = await fetch('/Student/ReportQuestionProblem', { method: 'POST', body: fd });
          var data = await res.json();
          if (!data.success) throw new Error(data.error || 'Could not send the report.');

          status.className = 'small mt-2 text-success';
          status.textContent = 'Thanks — your teacher will see this.';
          setTimeout(function () { if (modal) modal.hide(); }, 900);
        } catch (err) {
          status.className = 'small mt-2 text-danger';
          status.textContent = err.message;
        } finally {
          sendBtn.disabled = false;
        }
      });
    }
  }

  function token() {
    var el = document.querySelector('input[name="__RequestVerificationToken"]');
    return el ? el.value : '';
  }

  function escapeHtml(str) {
    var div = document.createElement('div');
    div.textContent = str || '';
    return div.innerHTML;
  }

  // escapeHtml() alone is not safe inside an HTML attribute value — it leaves
  // `"` untouched, which would close the attribute early (used for the
  // tooltip `title="…"` on a wrong step's ✗ mark).
  function escapeAttr(str) {
    return escapeHtml(str).replace(/"/g, '&quot;');
  }

})();
