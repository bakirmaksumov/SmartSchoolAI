/* ═══════════════════════════════════════════════════════════════════════════
   take-assignment.js – Question-by-question navigation, answer persistence,
   Tutorial AI chat with SSE streaming
   ═══════════════════════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  // ── State ────────────────────────────────────────────────────────────────
  let currentIndex   = 0;
  let totalQuestions = 0;
  let answers        = {};      // { "q_0": "value", "q_1": "A,B", ... }
  let questionTypes  = [];      // from data attribute
  let chatHistory     = [];
  let chatMode        = 'hint';
  let chatQuestionIdx = null;   // question the current conversation belongs to
  let chatAssignmentId = null;  // assignment the conversation belongs to
  let chatStep        = null;   // { number, text, feedback } when help was opened from one step
  let isSubmitting    = false;  // true once the quiz is being submitted (skip leave warning)
  let dirty           = false;  // student has entered/changed an answer but not submitted
  let wrongStreak     = {};     // { questionIndex: consecutive wrong "Check answer" clicks }
  const TUTOR_NUDGE_THRESHOLD = 2;

  // ── Initialise on DOM load ───────────────────────────────────────────────
  document.addEventListener('DOMContentLoaded', function () {
    const panels = document.querySelectorAll('.question-panel');
    totalQuestions = panels.length;
    if (totalQuestions === 0) return;

    // Gather question types from data attributes
    panels.forEach(function (p) {
      questionTypes.push(p.dataset.qtype || 'text');
    });

    showQuestion(pageLeads()[0]);
    bindNavigation();
    bindOptionCards();
    bindTextAreas();
    bindWorkPanels();
    bindMathToolbars();
    bindAntiPaste();
    hydrateAnswers();
    bindCheckAnswer();
    bindFormSubmit();
    bindLeaveGuards();
    renderAllMath();
  });

  // ── Instant per-question feedback ────────────────────────────────────────
  // The answer key stays on the server: we post the answer and only get a verdict.
  function bindCheckAnswer() {
    document.querySelectorAll('.check-answer-btn').forEach(function (btn) {
      btn.addEventListener('click', function () { checkAnswer(btn); });
    });
  }

  function feedbackBox(idx) {
    var panel = document.querySelector('.question-panel[data-qindex="' + idx + '"]');
    return panel ? panel.querySelector('.check-feedback') : null;
  }

  function checkButton(idx) {
    var panel = document.querySelector('.question-panel[data-qindex="' + idx + '"]');
    return panel ? panel.querySelector('.check-answer-btn') : null;
  }

  // ── What the student sees after checking: the answer and the working are judged separately ───────
  // checkState[idx] = {
  //   option: { token, correct, feedback, requireWork }     the verdict on the selected option / typed answer
  //   work:   { source, token, correct, firstError }        the verdict on checked working (typed steps or photo)
  // }
  // `token` is the answer (or working) the verdict was about. A verdict whose token no longer matches what is
  // on screen is stale and is never shown, so an old response cannot overwrite a newer state.
  var checkState = {};
  var checkSeq   = {};   // idx -> sequence number of the latest "Check answer" request

  function workValid(idx, w) {
    // Typed working is valid while it is unchanged; a checked photo stays valid until it is replaced.
    return w.source === 'typed' ? (answers['work_' + idx] || '') === w.token : true;
  }

  function currentWork(idx) {
    var st = checkState[idx];
    return st && st.work && workValid(idx, st.work) ? st.work : null;
  }

  function workPresent(idx) {
    return (answers['work_' + idx] || '').trim() !== '' || !!currentWork(idx);
  }

  function showFeedbackLines(box, kind, lines) {
    if (!box) return;
    var cls = kind === 'good' ? 'success' : kind === 'warn' ? 'warning' : 'danger';
    var icon = kind === 'good' ? 'bx-check-circle' : kind === 'warn' ? 'bx-error' : 'bx-x-circle';
    box.className = 'check-feedback alert mt-2 mb-0 py-2 px-3 alert-' + cls;
    // The icon leads the first line; any further line (a working error) sits under it.
    box.innerHTML = lines.map(function (l, i) {
      return '<span class="check-line d-block">' + (i === 0 ? '<i class="bx ' + icon + ' me-1"></i>' : '') + escapeHtml(l) + '</span>';
    }).join('');
    renderMathEl(box);
  }

  function clearFeedback(idx) {
    var box = feedbackBox(idx);
    if (box) { box.className = 'check-feedback alert d-none mt-2 mb-0 py-2 px-3'; box.innerHTML = ''; }
    var btn = checkButton(idx);
    if (btn) toggleTutorNudge(btn, false);
    window.QuizPills.setResult(idx, null);
  }

  function dropCheck(idx) {
    delete checkState[idx];
    clearFeedback(idx);
    saveChecks();
  }

  // Draws the banner, the pill and the tutor nudge for one question from its current state.
  function renderCheck(idx, opts) {
    var st = checkState[idx];
    if (!st || !st.option) return;
    var o = st.option;
    if (o.token !== (answers['q_' + idx] || '')) { dropCheck(idx); return; }

    var work = currentWork(idx);
    var kind, lines, pill;
    if (!o.correct) {
      kind = 'bad'; pill = 'wrong';
      lines = [o.feedback || 'Your answer is not correct. Check your work and try again.'];
    } else if (work && work.correct) {
      kind = 'good'; pill = 'correct';
      lines = ['Your answer and working are correct.'];
    } else if (work) {
      kind = 'warn'; pill = 'wrong';
      lines = ['Your answer is correct, but your working has a mistake.'].concat(work.firstError ? [work.firstError] : []);
    } else if (o.requireWork && !workPresent(idx)) {
      kind = 'good'; pill = 'correct';
      lines = ['Your choice is correct. Add your working to complete this question.'];
    } else {
      kind = 'good'; pill = 'correct';
      lines = [o.feedback || 'Your answer is correct! 🎉'];
    }

    showFeedbackLines(feedbackBox(idx), kind, lines);
    window.QuizPills.setResult(idx, pill);

    var btn = checkButton(idx);
    if (btn) toggleTutorNudge(btn, !o.correct && (wrongStreak[idx] || 0) >= TUTOR_NUDGE_THRESHOLD);
    if (opts && opts.fresh && kind === 'good' && lines[0].indexOf('Add your working') === -1) celebrate();
  }

  // The verdict of a "Check answer" (an instant key check or the AI's).
  function recordOption(idx, token, v) {
    var st = checkState[idx] = checkState[idx] || {};
    var panel = document.querySelector('.question-panel[data-qindex="' + idx + '"]');
    st.option = { token: token, correct: !!(v && v.correct), feedback: (v && v.feedback) || '',
                  requireWork: !!(v && v.requireWork) || (!!panel && panel.dataset.requirework === 'true' && QuizChecks.selectedAnswer(idx) !== '') };
    if (st.option.correct) wrongStreak[idx] = 0;
    else wrongStreak[idx] = (wrongStreak[idx] || 0) + 1;
    renderCheck(idx, { fresh: true });
    saveChecks();
  }

  // Checked working (typed steps or a photo) is recorded for ITS question and shown with the answer's verdict.
  function readableText(s) { return window.MathText ? MathText.inline(s || '').text : String(s || ''); }

  window.QuizChecks = {
    workToken: function (idx) { return answers['work_' + idx] || ''; },

    // For a choice question, the selected option(s) travel with the working so the server can judge the
    // option on its own (by the teacher's key, or the AI with the full choices). Empty for other questions.
    selectedAnswer: function (idx) {
      var panel = document.querySelector('.question-panel[data-qindex="' + idx + '"]');
      var type = panel ? panel.dataset.qtype : '';
      return (type === 'single' || type === 'multi') ? (answers['q_' + idx] || '') : '';
    },

    recordWork: function (idx, source, token, result) {
      var steps = (result && result.steps) || [];
      var bad = steps.filter(function (s) { return (s.status || '').toLowerCase() !== 'correct'; })[0];
      var correct = ((result && result.overall) || '').toLowerCase() === 'correct' && !bad;
      var firstError = bad
        ? 'Step ' + bad.number + ': ' + (readableText(bad.explanation) || 'this step is not correct.')
        : (!correct && result && result.summary ? readableText(result.summary) : '');

      var st = checkState[idx] = checkState[idx] || {};
      st.work = { source: source, token: token, correct: correct, firstError: firstError };

      // A fresh verdict on the selected option replaces the earlier one if the student has not changed the
      // answer since: this is what clears a stale red banner once the option is judged correctly.
      if (st.option && result && result.option && st.option.token === (answers['q_' + idx] || '')) {
        st.option.correct  = !!result.option.correct;
        st.option.feedback = result.option.feedback || '';
        if (st.option.correct) wrongStreak[idx] = 0;
      }

      if (st.option) renderCheck(idx);
      else window.QuizPills.setResult(idx, correct);   // no answer check to combine with: the working alone sets the pill
      window.QuizPills.setAnswered(idx, true);
      saveChecks();
    },

    // The photo was replaced or removed: its earlier verdict no longer describes what is on screen.
    invalidateWork: function (idx) {
      var st = checkState[idx];
      if (!st || !st.work) return;
      delete st.work;
      if (st.option) renderCheck(idx); else window.QuizPills.setResult(idx, null);
      saveChecks();
    }
  };

  function checksKey() { return 'quizChecks:' + (document.getElementById('assignmentId') || {}).value; }

  function saveChecks() {
    try { localStorage.setItem(checksKey(), JSON.stringify(checkState)); } catch (e) { /* best effort */ }
  }

  function clearChecks() {
    try { localStorage.removeItem(checksKey()); } catch (e) { /* ignore */ }
  }

  // After a reload, show again the verdicts that still describe what is on screen.
  function restoreChecks() {
    var stored = null;
    try { stored = JSON.parse(localStorage.getItem(checksKey()) || 'null'); } catch (e) { stored = null; }
    if (!stored) return;
    Object.keys(stored).forEach(function (k) {
      var idx = parseInt(k, 10);
      checkState[idx] = stored[k];
      if (checkState[idx].work) window.QuizPills.setAnswered(idx, true);
      if (checkState[idx].option) renderCheck(idx);
      else if (checkState[idx].work && workValid(idx, checkState[idx].work)) window.QuizPills.setResult(idx, checkState[idx].work.correct);
    });
  }

  async function checkAnswer(btn) {
    var idx  = parseInt(btn.dataset.qindex, 10);
    var box  = feedbackBox(idx);
    var answer = answers['q_' + idx] || '';

    if (!answer.trim()) {
      showFeedback(box, false, 'Please answer this question first.');
      return;
    }

    // This request is about THIS question and THIS answer. If the answer changes, or a newer check starts,
    // while it runs, its response is dropped instead of overwriting the newer state.
    var token = answer;
    var seq = checkSeq[idx] = (checkSeq[idx] || 0) + 1;
    var stillCurrent = function () { return checkSeq[idx] === seq && (answers['q_' + idx] || '') === token; };

    var original = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Checking…';

    var restoreBtn = function () { btn.disabled = false; btn.innerHTML = original; };

    // `v` is { correct, feedback, requireWork } — from an instant reply or a finished job.
    var applyVerdict = function (v) {
      restoreBtn();
      if (!stillCurrent()) return;
      recordOption(idx, token, v);
    };

    var fail = function (message) {
      restoreBtn();
      if (stillCurrent()) showFeedback(box, false, message);
    };

    try {
      var fd = new FormData();
      fd.append('assignmentId', document.getElementById('assignmentId').value);
      fd.append('questionIndex', idx);
      fd.append('answer', answer);
      fd.append('__RequestVerificationToken', antiForgeryToken());

      var res  = await fetch('/Student/CheckAnswer', { method: 'POST', body: fd });
      var data = await res.json();

      if (!data.success) {
        fail(data.error || 'Could not check your answer. Please try again.');
        return;
      }

      // An answer with a key comes back decided — no AI, no waiting, no game.
      if (data.status === 'completed') {
        applyVerdict(data);
        return;
      }

      // No key: the AI is judging it in the background. Play a game while polling, and hold the verdict
      // until the student taps "See result". Without the game module the verdict shows on arrival.
      if (window.WaitingGame && window.GradePoll) {
        WaitingGame.start();
        GradePoll.start(data.jobId, {
          onDone: function (result) { WaitingGame.finish(function () { applyVerdict(result); }); },
          onError: function (msg)   { WaitingGame.close(); fail(msg); }
        });
      } else if (window.GradePoll) {
        GradePoll.start(data.jobId, {
          onDone: applyVerdict,
          onError: fail
        });
      } else {
        fail('Could not check your answer. Please try again.');
      }
    } catch (err) {
      if (window.WaitingGame) WaitingGame.close();
      fail('Could not check your answer. Please check your connection and try again.');
    }
  }

  function showFeedback(box, ok, message) {
    if (!box) return;
    box.className = 'check-feedback alert mt-2 mb-0 py-2 px-3 alert-' + (ok ? 'success' : 'danger');
    box.innerHTML = '<i class="bx ' + (ok ? 'bx-check-circle' : 'bx-x-circle') + ' me-1"></i>' + escapeHtml(message);
    renderMathEl(box);
  }

  // A nudge toward the AI tutor after a couple of wrong tries on the same
  // question — a suggestion the student can act on or ignore, not a popup that
  // interrupts them.
  function toggleTutorNudge(btn, show) {
    var nudge = btn.closest('.question-panel').querySelector('.check-tutor-nudge');
    if (nudge) nudge.classList.toggle('d-none', !show);
  }

  // Colour the nav pill by the latest verdict for that question.
  function markPillResult(idx, ok) {
    window.QuizPills.setResult(idx, ok);
  }

  function celebrate() {
    if (typeof confetti !== 'function') return;   // library not loaded — skip quietly
    if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    confetti({ particleCount: 90, spread: 70, origin: { y: 0.7 }, disableForReducedMotion: true });
  }

  function antiForgeryToken() {
    var el = document.querySelector('input[name="__RequestVerificationToken"]');
    return el ? el.value : '';
  }

  function escapeHtml(str) {
    var div = document.createElement('div');
    div.textContent = str || '';
    return div.innerHTML;
  }

  // Warn before leaving mid-quiz with unsaved answers, and reset the tutor chat
  // when the page is restored from the browser's back/forward cache.
  function bindLeaveGuards() {
    window.addEventListener('beforeunload', function (e) {
      if (isSubmitting || !dirty) return;   // nothing unsaved, or an intentional submit
      // A non-empty string is the most reliable cross-browser trigger; the browser
      // shows its own generic "Leave site?" text and ignores ours.
      var msg = 'You have answers that are not submitted yet. If you leave, they will be lost.';
      e.preventDefault();
      e.returnValue = msg;
      return msg;
    });

    window.addEventListener('pageshow', function (e) {
      if (e.persisted) resetTutorial();   // restored from bfcache — start clean
    });

    // localStorage is shared across tabs on the same origin, so the SAME quiz
    // open in two tabs at once would silently overwrite each other's draft with
    // no sign anything went wrong. The 'storage' event fires in every OTHER tab
    // whenever one tab writes to localStorage (never in the tab that wrote it),
    // so seeing it for this exact assignment's draft key means another tab just
    // touched the same answers — surface that instead of staying silent.
    window.addEventListener('storage', function (e) {
      if (e.key !== draftKey()) return;
      // A null newValue means the other tab cleared the draft (it submitted) —
      // that's a normal end state, not a conflict, so nothing to warn about.
      if (!e.newValue) return;

      var banner = document.getElementById('otherTabWarning');
      if (banner) banner.classList.remove('d-none');
    });
  }

  // ── Navigation ───────────────────────────────────────────────────────────
  function showQuestion(idx) {
    if (idx < 0 || idx >= totalQuestions) return;
    var questionChanged = idx !== currentIndex;
    currentIndex = idx;

    // Panels: every panel of the page (a parent and all its parts) is shown together.
    document.querySelectorAll('.question-panel').forEach(function (p) {
      p.classList.toggle('active', parseInt(p.dataset.page, 10) === idx);
    });

    // Nav pills
    document.querySelectorAll('.question-nav .nav-btn[data-idx]').forEach(function (btn) {
      var bi = parseInt(btn.dataset.idx, 10);
      btn.classList.toggle('active', bi === idx);
    });

    // Prev / Next (top pills + bottom buttons)
    var leads = pageLeads();
    var pos   = leads.indexOf(idx);
    var prevBtn  = document.getElementById('prevBtn');
    var nextBtn  = document.getElementById('nextBtn');
    var bPrevBtn = document.getElementById('bottomPrevBtn');
    var bNextBtn = document.getElementById('bottomNextBtn');
    if (prevBtn)  prevBtn.disabled  = (pos <= 0);
    if (nextBtn)  nextBtn.disabled  = (pos === leads.length - 1);
    if (bPrevBtn) bPrevBtn.disabled = (pos <= 0);
    if (bNextBtn) bNextBtn.disabled = (pos === leads.length - 1);

    // Update "Question N of M" label
    var ql = document.getElementById('questionLabel');
    var lead = document.querySelector('.question-panel[data-qindex="' + idx + '"]');
    if (ql) ql.textContent = 'Question ' + (lead && lead.dataset.label ? lead.dataset.label : idx + 1);

    // A tutor conversation belongs to one question — moving to a different one
    // (Next/Previous, a nav pill, anything) must not leave the old question's
    // chat sitting on screen as if it were about the new one.
    if (questionChanged) onQuestionChangedForTutorial(idx);
  }

  // The first question index of every page, in order (one navigation pill each).
  function pageLeads() {
    var out = [];
    document.querySelectorAll('.question-nav .nav-btn[data-idx]').forEach(function (b) { out.push(parseInt(b.dataset.idx, 10)); });
    return out;
  }

  function pageOfIndex(i) {
    var p = document.querySelector('.question-panel[data-qindex="' + i + '"]');
    return p ? parseInt(p.dataset.page, 10) : i;
  }

  // The answerable parts of a page: every panel on it except a shared stem.
  function partsOfPage(lead) {
    var out = [];
    document.querySelectorAll('.question-panel[data-page="' + lead + '"]').forEach(function (p) {
      if (p.dataset.header !== 'true') out.push(parseInt(p.dataset.qindex, 10));
    });
    return out;
  }

  // The part the student is working in, so Tutorial help is about that part.
  var lastPart = null;
  document.addEventListener('focusin', trackPart);
  document.addEventListener('click', trackPart);
  function trackPart(e) {
    var panel = e.target && e.target.closest ? e.target.closest('.question-panel') : null;
    if (panel && panel.dataset.header !== 'true') lastPart = parseInt(panel.dataset.qindex, 10);
  }

  function activePartIndex() {
    var active = document.querySelectorAll('.question-panel.active');
    var first = null;
    for (var k = 0; k < active.length; k++) {
      var idx = parseInt(active[k].dataset.qindex, 10);
      if (active[k].dataset.header === 'true') continue;
      if (first === null) first = idx;
      if (idx === lastPart) return idx;
    }
    return first !== null ? first : (active.length ? parseInt(active[0].dataset.qindex, 10) : 0);
  }

  function bindNavigation() {
    // Number pills
    document.querySelectorAll('.question-nav .nav-btn[data-idx]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        showQuestion(parseInt(this.dataset.idx, 10));
      });
    });

    // Previous / Next (top pills + bottom buttons)
    var prevBtn  = document.getElementById('prevBtn');
    var nextBtn  = document.getElementById('nextBtn');
    var bPrevBtn = document.getElementById('bottomPrevBtn');
    var bNextBtn = document.getElementById('bottomNextBtn');
    // Previous / Next move between pages (one per top-level question), not between parts.
    var goPrev = function () { var l = pageLeads(); var p = l.indexOf(currentIndex); if (p > 0) showQuestion(l[p - 1]); };
    var goNext = function () { var l = pageLeads(); var p = l.indexOf(currentIndex); if (p >= 0 && p < l.length - 1) showQuestion(l[p + 1]); };
    if (prevBtn)  prevBtn.addEventListener('click', goPrev);
    if (nextBtn)  nextBtn.addEventListener('click', goNext);
    if (bPrevBtn) bPrevBtn.addEventListener('click', goPrev);
    if (bNextBtn) bNextBtn.addEventListener('click', goNext);
  }

  // ── MCQ Option Card clicks ───────────────────────────────────────────────
  function bindOptionCards() {
    document.querySelectorAll('.option-card').forEach(function (card) {
      card.addEventListener('click', function () {
        var qKey   = this.dataset.qkey;     // "q_0"
        var value  = this.dataset.value;    // the letter (A, B, C, D)
        var isMulti = this.dataset.multi === 'true';

        if (isMulti) {
          // Toggle checkbox behaviour
          this.classList.toggle('selected');
          var selected = [];
          this.closest('.options-list').querySelectorAll('.option-card.selected').forEach(function (c) {
            selected.push(c.dataset.value);
          });
          answers[qKey] = selected.join(',');
        } else {
          // Radio behaviour – deselect siblings
          this.closest('.options-list').querySelectorAll('.option-card').forEach(function (c) {
            c.classList.remove('selected');
          });
          this.classList.add('selected');
          answers[qKey] = value;
        }
        dirty = true;
        updateNavPill(qKey);
        syncHiddenInput(qKey);
        saveDraft();
      });
    });
  }

  // ── Field bindings (answers and "show your work") ─────────────────────────
  // ".answer-textarea"/".work-textarea" now mostly live on <math-field>
  // elements rather than <textarea> — a math-field's own .value is bare LaTeX
  // (no delimiters, since the whole field is already math), while every other
  // page that redisplays a saved answer expects the old "$…$"-delimited plain
  // text KaTeX auto-render already looks for. isMathField()/toStoredValue()
  // are the one seam that keeps that format unchanged everywhere else.
  function isMathField(el) {
    return el.tagName === 'MATH-FIELD';
  }

  // What a field's live value becomes once it leaves the field (answers{},
  // the hidden POST input, the session draft).
  function toStoredValue(el) {
    if (!isMathField(el)) return el.value;
    var v = (el.value || '').trim();
    return v ? '$' + v + '$' : '';
  }

  // The inverse, for putting a recovered/restored value back INTO a field.
  function fromStoredValue(el, stored) {
    if (!isMathField(el)) return stored;
    return String(stored || '').replace(/^\$|\$$/g, '');
  }

  function bindTextAreas() {
    document.querySelectorAll('.answer-textarea, .work-textarea').forEach(function (ta) {
      ta.addEventListener('input', function () {
        var qKey = this.dataset.qkey;
        answers[qKey] = toStoredValue(this);
        dirty = true;
        updateNavPill(qKey);
        syncHiddenInput(qKey);
        updateMathPreview(this);
        saveDraft();
      });
    });
  }

  // ── Anti copy-paste ──────────────────────────────────────────────────────
  // A soft deterrent against pasting an answer straight from a chatbot. It is
  // deliberately not airtight — it just makes the shortcut inconvenient.
  function bindAntiPaste() {
    var form = document.getElementById('assignmentForm');
    if (!form || form.dataset.antiPaste !== 'true') return;

    document.querySelectorAll('.answer-textarea, .work-textarea').forEach(function (ta) {
      ta.addEventListener('paste', function (e) {
        e.preventDefault();
        showPasteBlocked(ta);
      });
      ta.addEventListener('drop', function (e) { e.preventDefault(); });
    });
  }

  function showPasteBlocked(ta) {
    var note = ta.parentElement.querySelector('.paste-blocked-note');
    if (!note) {
      note = document.createElement('div');
      note.className = 'paste-blocked-note text-danger small mt-1';
      note.textContent = 'Pasting is turned off for this assignment — please type your answer.';
      ta.parentElement.insertBefore(note, ta.nextSibling);
    }
    note.classList.remove('d-none');
    clearTimeout(note.dataset.timer);
    note.dataset.timer = setTimeout(function () { note.classList.add('d-none'); }, 4000);
  }

  // ── "Show your work" panel ───────────────────────────────────────────────
  function bindWorkPanels() {
    document.querySelectorAll('.work-toggle').forEach(function (toggle) {
      toggle.addEventListener('change', function () {
        var panel = this.closest('.work-block').querySelector('.work-panel');
        if (panel) panel.classList.toggle('d-none', !this.checked);
      });

      // A required toggle renders pre-checked (and disabled) server-side, so
      // there's no user 'change' event to reveal its panel — sync it once here.
      if (toggle.checked) {
        var initialPanel = toggle.closest('.work-block').querySelector('.work-panel');
        if (initialPanel) initialPanel.classList.remove('d-none');
      }
    });

    document.querySelectorAll('.work-tab-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var panel = this.closest('.work-panel');
        var tab   = this.dataset.workTab;

        panel.querySelectorAll('.work-tab-btn').forEach(function (b) {
          b.classList.toggle('active', b.dataset.workTab === tab);
        });
        panel.querySelectorAll('.work-pane').forEach(function (p) {
          p.classList.toggle('d-none', p.dataset.workPane !== tab);
        });
      });
    });
  }

  // ── Math input toolbar (works for any .math-input wrapper) ───────────────
  // A wrapper can hold one <math-field> (the main answer box) or a list of
  // per-step <math-field>s (the typed step-by-step editor) — the toolbar
  // targets whichever field last had focus inside the wrapper, so both
  // shapes share one toolbar partial without knowing which they're in.
  function bindMathToolbars() {
    document.querySelectorAll('.math-toolbar').forEach(function (bar) {
      var block = bar.closest('.math-input');
      if (!block) return;

      var activeField = block.querySelector('math-field:not(.d-none)');
      block.addEventListener('focusin', function (e) {
        if (isMathField(e.target)) activeField = e.target;
      });

      bar.querySelectorAll('button').forEach(function (btn) {
        btn.addEventListener('click', function () {
          // The typed-steps editor holds plain text fields: insert readable symbols at the caret.
          if (block.querySelector('.type-steps-list')) { insertStepText(block, btn); return; }

          var field = activeField || block.querySelector('math-field');
          if (!field || typeof field.insert !== 'function') return;
          // `insertionMode: 'insertAfter'` places the caret right after the
          // inserted LaTeX; a bare {}/[] in it is MathLive's own placeholder,
          // so the caret ends up *inside* the slot on its own — "click the
          // power button, type into the exponent" needs nothing more.
          field.insert(btn.dataset.insert || '', { focus: true, selectionMode: 'placeholder' });
        });
      });
    });
  }

  // Inserts a toolbar button's readable form (data-text, e.g. "√()" with the caret at
  // data-caret) into the step field that last had focus, at the caret or over the selection.
  var lastStepInput = null;
  document.addEventListener('focusin', function (e) {
    if (e.target.classList && e.target.classList.contains('type-step-input')) lastStepInput = e.target;
  });

  function insertStepText(block, btn) {
    var input = lastStepInput && block.contains(lastStepInput) ? lastStepInput : block.querySelector('.type-step-input');
    var text = btn.dataset.text;
    if (!input || !text) return;

    var start = input.selectionStart == null ? input.value.length : input.selectionStart;
    var end   = input.selectionEnd == null ? start : input.selectionEnd;
    input.setRangeText(text, start, end, 'end');
    var caret = start + (btn.dataset.caret != null ? parseInt(btn.dataset.caret, 10) : text.length);
    input.focus();
    input.setSelectionRange(caret, caret);
    input.dispatchEvent(new Event('input', { bubbles: true }));
  }

  // Kept only so any leftover ".math-preview-wrap" markup elsewhere no-ops
  // harmlessly — a math-field renders itself live, it has no separate preview.
  function updateMathPreview(ta) {
    var block = ta.closest('.math-input');
    if (!block) return;
    var wrap = block.querySelector('.math-preview-wrap');
    var box  = block.querySelector('.math-preview');
    if (!wrap || !box) return;

    var text = ta.value.trim();
    if (!text) { wrap.classList.add('d-none'); return; }
    wrap.classList.remove('d-none');
    box.textContent = text;
    renderMathEl(box);
  }

  // ── Answer draft (localStorage) ──────────────────────────────────────────
  // Typed answers only reach the server on submit, so a reload — or closing the
  // browser entirely and coming back later — used to lose them. Keep a
  // per-assignment draft and restore it when the page comes back.
  //
  // localStorage, not sessionStorage: sessionStorage is cleared the moment the
  // browser (not just the tab) closes, which is exactly the case students hit —
  // close the laptop mid-quiz, come back tomorrow, draft gone. localStorage has
  // no such expiry; it only clears on submit (clearDraft, below) or if the
  // student clears their browser data.
  //
  // Trade-off: localStorage is shared across tabs on the same origin, so the
  // same quiz open in two tabs at once will overwrite each other's draft. A
  // student is not expected to work the same assignment in two tabs, so this
  // is accepted in exchange for surviving a full browser close.

  function draftKey() {
    var el = document.getElementById('assignmentId');
    return 'quizDraft:' + (el ? el.value : '');
  }

  function saveDraft() {
    try { localStorage.setItem(draftKey(), JSON.stringify(answers)); }
    catch (e) { /* storage unavailable — draft is best-effort */ }
  }

  function loadDraft() {
    try {
      var raw = localStorage.getItem(draftKey());
      return raw ? JSON.parse(raw) : null;
    } catch (e) { return null; }
  }

  function clearDraft() {
    clearChecks();
    try { localStorage.removeItem(draftKey()); } catch (e) { /* ignore */ }
  }

  // ── Sync hidden form field so the <form> posts correctly ─────────────────
  function syncHiddenInput(qKey) {
    var hidden = document.getElementById('hidden_' + qKey);
    if (hidden) {
      hidden.value = answers[qKey] || '';
    }
  }

  // ── Mark pill as answered ────────────────────────────────────────────────
  function updateNavPill(qKey) {
    if (qKey.indexOf('q_') !== 0) return;   // "work_*" keys don't affect the pills
    var idx = parseInt(qKey.replace('q_', ''), 10);
    refreshPill(idx);
    // A changed answer makes an earlier check of the old one stale.
    if (checkState[idx] && checkState[idx].option && checkState[idx].option.token !== (answers[qKey] || '')) dropCheck(idx);
  }

  // ── Navigation pills: one per page ───────────────────────────────────────
  // A pill covers every answerable part of its page: "answered" when all are, "partial" when only some
  // are, "correct" when every part checked correct, "wrong" when any part checked wrong.
  var pillAnswered = {};   // question index -> answered by working/photo even without a main answer
  var pillResult   = {};   // question index -> 'correct' | 'wrong'

  function partAnswered(i) {
    return !!(answers['q_' + i] && answers['q_' + i].length > 0) || !!pillAnswered[i];
  }

  function refreshPill(idx) {
    var lead = pageOfIndex(idx);
    var btn  = document.querySelector('.question-nav .nav-btn[data-idx="' + lead + '"]');
    var parts = partsOfPage(lead);
    if (!btn || parts.length === 0) return;

    var answered = parts.filter(partAnswered).length;
    btn.classList.toggle('answered', answered === parts.length);
    btn.classList.toggle('partial', answered > 0 && answered < parts.length);
    btn.classList.toggle('wrong', parts.some(function (i) { return pillResult[i] === 'wrong'; }));
    btn.classList.toggle('correct', parts.every(function (i) { return pillResult[i] === 'correct'; }));
  }

  window.QuizPills = {
    setAnswered: function (idx, on) { pillAnswered[idx] = !!on; refreshPill(idx); },
    setResult:   function (idx, result) {
      if (result === true) result = 'correct'; else if (result === false) result = 'wrong';
      if (result) pillResult[idx] = result; else delete pillResult[idx];
      refreshPill(idx);
    }
  };

  // ── Recover answers the browser restored on a reload ─────────────────────
  // After a page reload the browser may repopulate form fields while the JS
  // `answers` state starts empty. Rebuild `answers` from the DOM so the
  // unanswered count, pills and submission all stay consistent.
  function hydrateAnswers() {
    document.querySelectorAll('.answer-textarea, .work-textarea').forEach(function (ta) {
      if ((ta.value || '').trim() !== '') answers[ta.dataset.qkey] = toStoredValue(ta);
    });

    for (var i = 0; i < totalQuestions; i++) {
      var h = document.getElementById('hidden_q_' + i);
      if (h && (h.value || '').trim() !== '' && !answers['q_' + i]) answers['q_' + i] = h.value;

      var w = document.getElementById('hidden_work_' + i);
      if (w && (w.value || '').trim() !== '' && !answers['work_' + i]) answers['work_' + i] = w.value;
    }

    // Our own draft survives a reload even when the browser restores nothing.
    var draft = loadDraft();
    if (draft) {
      Object.keys(draft).forEach(function (k) {
        if (!answers[k] || answers[k].toString().trim() === '') answers[k] = draft[k];
      });
    }

    // Put recovered text back into the fields it came from.
    Object.keys(answers).forEach(function (k) {
      var value = (answers[k] || '').toString();
      if (!value.trim()) return;

      var ta = document.querySelector('.answer-textarea[data-qkey="' + k + '"], .work-textarea[data-qkey="' + k + '"]');
      if (ta && !ta.value) {
        ta.value = fromStoredValue(ta, value);
        updateMathPreview(ta);

        // Reveal the "Show your work" panel if that's where the text belongs.
        var block = ta.closest('.work-block');
        if (block) {
          var toggle = block.querySelector('.work-toggle');
          var panel  = block.querySelector('.work-panel');
          if (toggle) toggle.checked = true;
          if (panel)  panel.classList.remove('d-none');
        }
      }
    });

    // Re-mark selected option cards from the recovered answers.
    document.querySelectorAll('.option-card').forEach(function (card) {
      var stored = (answers[card.dataset.qkey] || '').split(',');
      if (stored.indexOf(card.dataset.value) !== -1) card.classList.add('selected');
    });

    Object.keys(answers).forEach(function (k) {
      syncHiddenInput(k);
      updateNavPill(k);
    });

    restoreChecks();
  }

  // ── Form submit confirmation ─────────────────────────────────────────────
  // A submit ends the quiz for good, so guard it behind a deliberate modal
  // (prevents accidental clicks). Falls back to a native confirm if needed.
  function bindFormSubmit() {
    var form      = document.getElementById('assignmentForm');
    var submitBtn = document.getElementById('submitBtn');
    if (!form || !submitBtn) return;   // e.g. preview mode has no submit button

    var modalEl = document.getElementById('submitConfirmModal');

    form.addEventListener('submit', function (e) {
      e.preventDefault();

      // Count unanswered from the hidden inputs — those are exactly what gets
      // submitted (the browser may have restored them after a reload).
      var unanswered = 0;
      for (var i = 0; i < totalQuestions; i++) {
        var h = document.getElementById('hidden_q_' + i);
        if (h && h.dataset.header === 'true') continue;   // a shared stem has nothing to answer
        if (!h || (h.value || '').trim() === '') unanswered++;
      }
      var warnEl = document.getElementById('submitWarnText');
      if (warnEl) {
        warnEl.textContent = unanswered > 0
          ? 'You still have ' + unanswered + ' unanswered question(s).'
          : '';
      }

      if (modalEl && window.bootstrap) {
        bootstrap.Modal.getOrCreateInstance(modalEl).show();
      } else if (confirm('Submit and finish? You cannot come back or change your answers.')) {
        isSubmitting = true;
        clearDraft();   // answers are about to be saved server-side
        form.submit();
      }
    });

    var confirmBtn = document.getElementById('confirmSubmitBtn');
    if (confirmBtn) {
      confirmBtn.addEventListener('click', function () {
        if (modalEl && window.bootstrap) bootstrap.Modal.getOrCreateInstance(modalEl).hide();
        isSubmitting = true;
        clearDraft();   // answers are about to be saved server-side
        form.submit();   // native submit — bypasses the handler above, posts directly
      });
    }
  }

  // ── KaTeX rendering ──────────────────────────────────────────────────────
  var MATH_DELIMS = [
    { left: '$$', right: '$$', display: true },
    { left: '$',  right: '$',  display: false },
    { left: '\\(', right: '\\)', display: false },
    { left: '\\[', right: '\\]', display: true }
  ];

  function renderMathEl(el) {
    if (el && typeof renderMathInElement === 'function') {
      renderMathInElement(el, { delimiters: MATH_DELIMS, throwOnError: false });
    }
  }

  function renderAllMath() {
    document.querySelectorAll('.math-render').forEach(renderMathEl);
  }

  // ═══════════════════════════════════════════════════════════════════════════
  //  TUTORIAL AI CHAT
  // ═══════════════════════════════════════════════════════════════════════════

  // Expose globally so the button onclick works
  window.openTutorial = function (opts) {
    // Inline onclick handlers call this with no arguments; the step buttons pass { step }.
    return openTutorial(opts && opts.step ? opts : undefined);
  };
  window.closeTutorial = closeTutorial;
  window.setTutorialMode = setTutorialMode;

  function openTutorial(opts) {
    // Help opened from a step starts its own conversation about that step.
    var fromStep = opts && opts.step ? opts.step : null;
    var currentAssignment = document.getElementById('assignmentId') ? document.getElementById('assignmentId').value : '';
    var qIdx = activePartIndex();
    var q = document.querySelector('.question-panel[data-qindex="' + qIdx + '"]');

    positionTutorial();
    var panel   = document.getElementById('tutorialPanel');
    var overlay = document.getElementById('tutorialOverlay');
    if (panel)   panel.classList.add('open');
    if (overlay) overlay.classList.add('open');
    document.body.classList.add('tutorial-open');

    // Reopen the same conversation only for the SAME assignment AND question.
    var sameConversation = !fromStep
                        && chatAssignmentId === currentAssignment
                        && chatQuestionIdx === qIdx
                        && chatHistory.length > 0;
    if (sameConversation) {
      updateModeButtons();
      return;
    }

    // New assignment or question → start a fresh conversation.
    chatAssignmentId = currentAssignment;
    chatQuestionIdx = qIdx;
    chatStep = fromStep;
    chatHistory = [];
    chatMode = 'hint';
    updateModeButtons();

    var msgContainer = document.getElementById('tutorialMessages');
    if (msgContainer) msgContainer.innerHTML = '';

    var qTitle = q ? q.dataset.qtitle : '';
    var contextMsg = 'I need help with Question ' + (qIdx + 1) + ': "' + qTitle + '"';
    var ans = answers['q_' + qIdx] || '';
    if (ans) contextMsg += '. My current answer is: ' + ans;
    var working = tutorWork(qIdx);
    if (working.length) contextMsg += '. My working so far: ' + working.map(function (s, i) { return (i + 1) + ') ' + s; }).join(' ');
    if (chatStep) {
      contextMsg += '. I need help with step ' + chatStep.number + ': "' + chatStep.text + '"';
      if (chatStep.feedback) contextMsg += ' (the checker said: ' + chatStep.feedback + ')';
    }

    streamTutorTurn(contextMsg, false);  // hidden priming; assistant replies with the first hint
  }

  function closeTutorial() {
    var panel   = document.getElementById('tutorialPanel');
    var overlay = document.getElementById('tutorialOverlay');
    if (panel)   panel.classList.remove('open');
    if (overlay) overlay.classList.remove('open');
    document.body.classList.remove('tutorial-open');
  }

  // Called whenever the visible question changes (Next/Previous, a nav pill,
  // anything). A conversation belongs to one question, so if it belonged to a
  // different one, wipe it — and if the panel is currently open, immediately
  // start a fresh conversation for the question now on screen instead of
  // leaving the old one sitting there looking like it's about the new one.
  function onQuestionChangedForTutorial(idx) {
    // idx is a page; a chat about any part of the same page stays valid.
    if (chatQuestionIdx === null || pageOfIndex(chatQuestionIdx) === idx) return;

    var panel = document.getElementById('tutorialPanel');
    var panelOpen = !!(panel && panel.classList.contains('open'));

    chatHistory = [];
    chatQuestionIdx = null;
    chatStep = null;
    chatMode = 'hint';
    var msgs = document.getElementById('tutorialMessages');
    if (msgs) msgs.innerHTML = '';

    if (panelOpen) openTutorial();
  }

  // Wipes the conversation entirely (used on a fresh visit / bfcache restore).
  function resetTutorial() {
    chatHistory = [];
    chatQuestionIdx = null;
    chatAssignmentId = null;
    chatMode = 'hint';
    var msgs = document.getElementById('tutorialMessages');
    if (msgs) msgs.innerHTML = '';
    closeTutorial();
  }

  // Switching Hint / Explain / Solution continues the SAME conversation with a
  // new request — it never wipes the history for this task.
  function setTutorialMode(mode) {
    if (mode === chatMode) return;
    chatMode = mode;
    updateModeButtons();

    var intent = mode === 'solution' ? 'Can you show me the full solution?'
               : mode === 'concept'  ? 'Can you explain the concept behind this?'
               :                       'Can you give me a hint?';
    streamTutorTurn(intent, true);
  }

  function updateModeButtons() {
    document.querySelectorAll('.tutorial-modes .mode-btn').forEach(function (b) {
      b.classList.toggle('active', b.dataset.mode === chatMode);
    });
  }

  // Keeps the slide-in panel below the top navbar so it never overlays the menu.
  function positionTutorial() {
    var nav = document.getElementById('layout-navbar');
    var offset = nav ? Math.max(0, Math.round(nav.getBoundingClientRect().bottom)) : 0;
    ['tutorialPanel', 'tutorialOverlay'].forEach(function (id) {
      var el = document.getElementById(id);
      if (!el) return;
      el.style.top = offset + 'px';
      el.style.height = 'calc(100vh - ' + offset + 'px)';
      // On phones 100vh includes the browser bar and would push the composer off screen.
      if (window.CSS && CSS.supports && CSS.supports('height', '100dvh')) {
        el.style.height = 'calc(100dvh - ' + offset + 'px)';
      }
    });
  }

  // ── SSE Streaming helpers ────────────────────────────────────────────────

  // The server sends each chunk as a JSON string so newlines and edge whitespace
  // survive the SSE framing; anything that is not a JSON string is taken literally.
  function decodeSseChunk(payload) {
    try {
      var value = JSON.parse(payload);
      if (typeof value === 'string') return value;
    } catch (e) { /* not JSON — use as is */ }
    return payload;
  }

  async function readSseStream(response, onChunk) {
    var reader  = response.body.getReader();
    var decoder = new TextDecoder();
    var buffer  = '';

    while (true) {
      var result = await reader.read();
      if (result.done) break;

      buffer += decoder.decode(result.value, { stream: true });

      var newlineIdx;
      while ((newlineIdx = buffer.indexOf('\n')) !== -1) {
        // Only the line terminator is stripped: a chunk may legitimately end in a space.
        var line = buffer.slice(0, newlineIdx).replace(/\r$/, '');
        buffer   = buffer.slice(newlineIdx + 1);

        if (!line.startsWith('data: ')) continue;
        var payload = line.slice(6);
        if (!payload || payload === '[DONE]') continue;

        onChunk(decodeSseChunk(payload));
      }
    }
  }

  function createAssistantBubble() {
    var bubbleId     = 'bubble-' + Date.now();
    var msgContainer = document.getElementById('tutorialMessages');

    var msgWrapper       = document.createElement('div');
    msgWrapper.className = 'chat-msg assistant';

    var avatarSpan       = document.createElement('span');
    avatarSpan.className = 'msg-avatar';
    avatarSpan.innerHTML = '<i class="bx bx-bot" style="font-size:18px;color:#696cff;"></i>';

    var bubbleDiv       = document.createElement('div');
    bubbleDiv.className = 'msg-bubble assistant';
    bubbleDiv.id        = bubbleId;
    bubbleDiv.textContent = '▋';

    var timeDiv       = document.createElement('div');
    timeDiv.className = 'msg-time';
    timeDiv.textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

    var bubbleWrapper       = document.createElement('div');
    bubbleWrapper.className = 'msg-content';
    bubbleWrapper.appendChild(bubbleDiv);
    bubbleWrapper.appendChild(timeDiv);

    msgWrapper.appendChild(avatarSpan);
    msgWrapper.appendChild(bubbleWrapper);

    if (msgContainer) {
      msgContainer.appendChild(msgWrapper);
      msgContainer.scrollTop = msgContainer.scrollHeight;
    }

    return bubbleId;
  }

  function addUserBubble(text) {
    var msgContainer = document.getElementById('tutorialMessages');

    var msgWrapper       = document.createElement('div');
    msgWrapper.className = 'chat-msg user';

    var avatarSpan       = document.createElement('span');
    avatarSpan.className = 'msg-avatar';
    avatarSpan.innerHTML = '<i class="bx bx-user" style="font-size:18px;color:#696cff;"></i>';

    var bubbleDiv       = document.createElement('div');
    bubbleDiv.className = 'msg-bubble user';
    bubbleDiv.textContent = text;

    var timeDiv       = document.createElement('div');
    timeDiv.className = 'msg-time';
    timeDiv.textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

    var bubbleWrapper       = document.createElement('div');
    bubbleWrapper.className = 'msg-content';
    bubbleWrapper.appendChild(bubbleDiv);
    bubbleWrapper.appendChild(timeDiv);

    msgWrapper.appendChild(avatarSpan);
    msgWrapper.appendChild(bubbleWrapper);

    if (msgContainer) {
      msgContainer.appendChild(msgWrapper);
      msgContainer.scrollTop = msgContainer.scrollHeight;
    }
  }

  // The student's working for a question as a list of steps (one per line of the hidden mirror).
  function tutorWork(qIdx) {
    return (answers['work_' + qIdx] || '').split('\n').map(function (s) { return s.trim(); })
      .filter(function (s) { return s !== ''; });
  }

  // What the tutor must know to help with THIS question right now: sent with every turn, so the
  // latest answer and working are always the current ones.
  function tutorContextFields() {
    var idx = chatQuestionIdx;
    if (idx === null || idx === undefined) return {};
    var fields = {
      questionIndex: idx,
      answer:        answers['q_' + idx] || '',
      work:          tutorWork(idx)
    };
    if (chatStep) {
      fields.stepNumber   = chatStep.number;
      fields.stepText     = chatStep.text;
      fields.stepFeedback = chatStep.feedback || '';
    }
    return fields;
  }

  // Markdown prose and maths → readable DOM (never innerHTML: the reply is untrusted).
  function renderTutorText(bubble, text) {
    MathText.renderInto(bubble, text);
  }

  // Sends one tutor turn: appends the user message, streams the assistant reply,
  // and keeps the running history so the conversation continues across modes.
  async function streamTutorTurn(userMessage, showUserBubble) {
    if (showUserBubble) addUserBubble(userMessage);
    chatHistory.push({ role: 'user', content: userMessage });

    var bubbleId = createAssistantBubble();
    var msgContainer = document.getElementById('tutorialMessages');

    try {
      var assignmentId = document.getElementById('assignmentId')?.value || '';
      var csrfToken = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
      var response = await fetch('/Student/ChatStream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify(Object.assign({
          message:      userMessage,
          mode:         chatMode,
          assignmentId: assignmentId,
          history:      chatHistory.slice(0, -1)
        }, tutorContextFields()))
      });

      if (!response.ok) {
        throw new Error(response.status === 429
          ? 'Too many questions in a short time. Please wait a moment and try again.'
          : 'The tutor is unavailable right now (' + response.status + ').');
      }

      // The raw text is accumulated untouched and re-rendered from scratch on every chunk,
      // so a chunk that ends in a space or starts mid-word can never garble what came before.
      var fullText = '';
      await readSseStream(response, function (chunk) {
        fullText += chunk;
        var bubble = document.getElementById(bubbleId);
        if (bubble) {
          renderTutorText(bubble, fullText);
          if (msgContainer) msgContainer.scrollTop = msgContainer.scrollHeight;
        }
      });

      chatHistory.push({ role: 'assistant', content: fullText });

      // Notation that could not be converted to readable text is shown formatted.
      var done = document.getElementById(bubbleId);
      if (done) MathText.typeset(done);

    } catch (err) {
      var bubble = document.getElementById(bubbleId);
      if (bubble) bubble.textContent = '⚠️ ' + err.message;
    }
  }

  // ── Send user message ────────────────────────────────────────────────────
  window.sendTutorialMessage = async function () {
    var input = document.getElementById('tutorialInput');
    if (!input) return;

    var userMessage = input.value.trim();
    if (!userMessage) return;
    input.value = '';

    var sendBtn = document.getElementById('tutorialSendBtn');
    if (sendBtn) sendBtn.disabled = true;

    try {
      await streamTutorTurn(userMessage, true);
    } finally {
      if (sendBtn) sendBtn.disabled = false;
      input.focus();
    }
  };

  // Enter key to send
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Enter' && e.target.id === 'tutorialInput') {
      e.preventDefault();
      window.sendTutorialMessage();
    }
  });

})();
