/* ═══════════════════════════════════════════════════════════════════════════
   handwriting.js – Photo-solution flow for a single question.
   Two passes: AnalyzeHandwritten (OCR) → student edits → SubmitHandwritten (grade).
   Self-contained module; all DOM ids are prefixed "hw" to avoid collisions.
   ═══════════════════════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  // ── State ──────────────────────────────────────────────────────────────────
  let assignmentId  = '';
  let questionIndex = 0;
  let imagePaths    = [];        // page paths returned by AnalyzeHandwritten, replayed to SubmitHandwritten
  let files         = [];        // selected image files (one or more pages of the solution), already cropped
  let stream        = null;      // active webcam MediaStream
  let modal         = null;      // bootstrap.Modal instance
  let progressTimer = null;      // estimated-progress ticker while the AI works
  let cropQueue     = [];        // files picked/captured but not yet through the crop stage
  let cropQueueTotal = 0;        // for the "page N of M" label, reset per batch
  let cropper       = null;      // active Cropper.js instance for the crop stage
  let cropImageUrl  = null;      // object URL behind #hwCropImage's current src, revoked once done with it
  let waitGame      = null;      // tic-tac-toe played during the AI wait, or null if unavailable

  const $ = (id) => document.getElementById(id);

  document.addEventListener('DOMContentLoaded', function () {
    const modalEl = $('hwModal');
    if (!modalEl) return;       // page has no handwriting UI

    modal = new bootstrap.Modal(modalEl);
    modalEl.addEventListener('hidden.bs.modal', resetModal);

    // A game to play while OCR and grading run. Unlike the typed-answer flow, this
    // wait already lives in a modal, so the game is embedded in the loading stage
    // rather than opening a second modal on top.
    if (window.WaitingGame && $('hwWaitBoard')) {
      waitGame = WaitingGame.attach($('hwWaitBoard'), $('hwWaitStatus'));
      $('hwWaitNewGame').addEventListener('click', function () { waitGame.newGame(); });
    }

    bindOpenButtons();
    bindTabs();
    bindFilePicker();
    bindWebcam();
    bindActions();
  });

  // ── Open / reset ────────────────────────────────────────────────────────────
  function bindOpenButtons() {
    document.querySelectorAll('[data-hw-open]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        assignmentId  = $('assignmentId').value;
        questionIndex = parseInt(this.dataset.qindex, 10);
        $('hwQuestionTitle').textContent = this.dataset.qtitle || '';

        // Restore a saved draft so the student never has to re-analyze.
        var draft = loadDraft();
        if (draft && draft.imagePaths && draft.imagePaths.length > 0) {
          imagePaths = draft.imagePaths;
          renderEditableSteps(draft.steps || []);
          showStage('edit');
        } else {
          showStage('upload');
        }

        modal.show();
      });
    });
  }

  function resetModal() {
    // Closed after submitting → the work is saved server-side, drop the draft.
    // Closed mid-edit → keep the recognized steps (with any edits) as a draft.
    var resultsVisible = !$('hwStage-results').classList.contains('d-none');
    if (resultsVisible) clearDraft();
    else                saveDraft();

    stopProgress();
    stopCamera();
    destroyCropper();
    imagePaths = [];
    files = [];
    cropQueue = [];
    $('hwFileInput').value = '';
    renderPreviews();
    $('hwStepsList').innerHTML = '';
    $('hwResults').innerHTML = '';
    $('hwAnalyzeBtn').disabled = true;
    switchTab('file');
  }

  /** Shows exactly one stage: 'upload' | 'crop' | 'loading' | 'edit' | 'results'. */
  function showStage(stage) {
    ['upload', 'crop', 'loading', 'edit', 'results'].forEach(function (s) {
      $('hwStage-' + s).classList.toggle('d-none', s !== stage);
    });
  }

  // ── Estimated progress ───────────────────────────────────────────────────
  // The AI request is opaque (no real progress events), so we show an estimate:
  // the bar eases toward ~92% over the typical duration and snaps to 100% when
  // the response arrives. An elapsed-seconds counter tells the student it's alive.

  function setProgress(value) {
    var r = Math.min(100, Math.max(0, Math.round(value)));
    var bar = $('hwProgressBar'), pct = $('hwProgressPct');
    if (bar) { bar.style.width = r + '%'; bar.setAttribute('aria-valuenow', r); }
    if (pct) pct.textContent = r + '%';
  }

  function startProgress(label) {
    stopProgress();
    var lbl = $('hwProgressLabel');
    if (lbl && label) lbl.textContent = label;

    if (waitGame) waitGame.newGame();   // fresh board for this wait

    const start = Date.now();
    let value = 0;
    setProgress(0);
    if ($('hwProgressElapsed')) $('hwProgressElapsed').textContent = '0s';

    progressTimer = setInterval(function () {
      const elapsed = (Date.now() - start) / 1000;
      // Decelerating ease toward 92% (≈18s time constant).
      const target = 92 * (1 - Math.exp(-elapsed / 18));
      value = Math.max(value, target);
      setProgress(value);
      if ($('hwProgressElapsed')) $('hwProgressElapsed').textContent = Math.floor(elapsed) + 's';
    }, 200);
  }

  function stopProgress() {
    if (progressTimer) { clearInterval(progressTimer); progressTimer = null; }
  }

  function finishProgress() {
    stopProgress();
    setProgress(100);
  }

  // ── OCR draft (localStorage) ─────────────────────────────────────────────
  // Recognized steps cost an AI call, so they are kept as a per-question draft
  // until submitted. An accidental modal close, page reload, or closing the
  // browser and coming back later all restore them instead of forcing the
  // student to re-upload and pay for OCR again.
  //
  // localStorage, not sessionStorage: sessionStorage is wiped the moment the
  // browser itself closes, not just the tab — exactly when losing a paid-for
  // OCR result is most costly. The uploaded photos this draft references stay
  // on the server regardless of how long the draft sits unsubmitted, so there's
  // no staleness risk in keeping it around longer.

  function draftKey() {
    return 'hwDraft:' + assignmentId + ':' + questionIndex;
  }

  function saveDraft() {
    var steps = collectSteps();
    if (imagePaths.length === 0 || steps.length === 0) return;
    try {
      localStorage.setItem(draftKey(), JSON.stringify({ imagePaths: imagePaths, steps: steps }));
    } catch (e) { /* storage unavailable — draft is best-effort */ }
  }

  function loadDraft() {
    try {
      var raw = localStorage.getItem(draftKey());
      return raw ? JSON.parse(raw) : null;
    } catch (e) { return null; }
  }

  function clearDraft() {
    try { localStorage.removeItem(draftKey()); } catch (e) { /* ignore */ }
  }

  // ── Upload tabs ─────────────────────────────────────────────────────────────
  function bindTabs() {
    document.querySelectorAll('[data-hw-tab]').forEach(function (btn) {
      btn.addEventListener('click', function () { switchTab(this.dataset.hwTab); });
    });
  }

  function switchTab(tab) {
    document.querySelectorAll('[data-hw-tab]').forEach(function (b) {
      b.classList.toggle('active', b.dataset.hwTab === tab);
    });
    $('hwPanel-file').classList.toggle('d-none', tab !== 'file');
    $('hwPanel-webcam').classList.toggle('d-none', tab !== 'webcam');
    if (tab !== 'webcam') stopCamera();
  }

  // ── File picker ───────────────────────────────────────────────────────────--
  function bindFilePicker() {
    const dropZone = $('hwDropZone');
    const input    = $('hwFileInput');

    dropZone.addEventListener('click', function () { input.click(); });
    input.addEventListener('change', function () {
      addFiles(input.files);
      input.value = '';   // allow re-picking the same file / adding more pages
    });

    ['dragover', 'dragleave', 'drop'].forEach(function (evt) {
      dropZone.addEventListener(evt, function (e) {
        e.preventDefault();
        dropZone.classList.toggle('dragover', evt === 'dragover');
        if (evt === 'drop' && e.dataTransfer.files.length) addFiles(e.dataTransfer.files);
      });
    });
  }

  /**
   * Queues newly picked/captured images for cropping (dedupes by name+size)
   * and starts the crop stage — a photo never reaches OCR uncropped. The
   * queue, not `files` directly, is what grows here; each entry moves into
   * `files` only once the student confirms a crop or explicitly skips it.
   */
  function addFiles(fileList) {
    const before = cropQueue.length + files.length;
    Array.from(fileList).forEach(function (f) {
      if (!f.type.startsWith('image/')) return;
      const dup = files.some(function (x) { return x.name === f.name && x.size === f.size; })
               || cropQueue.some(function (x) { return x.name === f.name && x.size === f.size; });
      if (!dup) cropQueue.push(f);
    });
    if (cropQueue.length + files.length === before) return;   // nothing new (all dupes)

    cropQueueTotal = cropQueue.length + files.length;
    if (!cropper) processCropQueue();   // a crop already in progress will get to it
  }

  function removeFile(index) {
    files.splice(index, 1);
    renderPreviews();
  }

  // ── Crop stage ────────────────────────────────────────────────────────────
  // One photo at a time: load it into Cropper.js, let the student drag the
  // crop box, then either "Crop & continue" (cropped canvas → new File) or
  // "Use full photo" (original File, unchanged) — either way the file only
  // reaches `files[]`, and later OCR, after this stage has been shown once.
  function processCropQueue() {
    if (cropQueue.length === 0) {
      showStage('upload');
      renderPreviews();
      return;
    }

    const file = cropQueue[0];
    const img  = $('hwCropImage');

    $('hwCropPageNum').textContent = String(cropQueueTotal - cropQueue.length + 1);
    $('hwCropPageTotal').textContent = String(cropQueueTotal);

    destroyCropper();
    // The object URL is revoked in advanceCropQueue(), once this photo's crop
    // is confirmed — not here on load. Cropper.js reads the image via its own
    // async fetch of this same `src` to build the crop canvas; revoking the
    // URL right after `onload` (safe for a plain <img>, which only needs it
    // for one synchronous decode) invalidated it before that fetch ran, and
    // the crop stage silently never appeared.
    cropImageUrl = URL.createObjectURL(file);
    img.src = cropImageUrl;
    img.onload = function () {
      if (typeof Cropper === 'undefined') {
        // Library failed to load (offline, blocked CDN) — don't strand the
        // student on a dead screen; fall back to using the photo uncropped.
        useFullPhoto();
        return;
      }
      cropper = new Cropper(img, { viewMode: 1, autoCropArea: 1, background: false });
    };

    showStage('crop');
  }

  function destroyCropper() {
    if (cropper) { cropper.destroy(); cropper = null; }
    if (cropImageUrl) { URL.revokeObjectURL(cropImageUrl); cropImageUrl = null; }
  }

  function confirmCrop() {
    if (!cropper) { useFullPhoto(); return; }
    const file = cropQueue[0];

    cropper.getCroppedCanvas().toBlob(function (blob) {
      if (!blob) { useFullPhoto(); return; }
      files.push(new File([blob], file.name, { type: blob.type || file.type }));
      advanceCropQueue();
    }, file.type || 'image/png');
  }

  function useFullPhoto() {
    const file = cropQueue[0];
    if (file) files.push(file);
    advanceCropQueue();
  }

  function advanceCropQueue() {
    destroyCropper();
    cropQueue.shift();
    processCropQueue();
  }

  /** Renders a thumbnail (with page number + remove button) for every selected file. */
  function renderPreviews() {
    const wrap = $('hwPreview');
    if (!wrap) return;
    wrap.innerHTML = '';
    wrap.classList.toggle('d-none', files.length === 0);

    files.forEach(function (file, i) {
      const url  = URL.createObjectURL(file);
      const cell = document.createElement('div');
      cell.className = 'hw-thumb';
      cell.innerHTML =
        '<img alt="page ' + (i + 1) + '" />' +
        '<span class="hw-thumb-page">' + (i + 1) + '</span>' +
        '<button type="button" class="hw-thumb-del" title="Remove">&times;</button>';
      const img = cell.querySelector('img');
      img.src = url;
      img.onload = function () { URL.revokeObjectURL(url); };
      cell.querySelector('.hw-thumb-del').addEventListener('click', function () { removeFile(i); });
      wrap.appendChild(cell);
    });

    $('hwAnalyzeBtn').disabled = files.length === 0;
  }

  // ── Webcam (scoped) ───────────────────────────────────────────────────────--
  function bindWebcam() {
    $('hwCamStart').addEventListener('click', startCamera);
    $('hwCamCapture').addEventListener('click', captureFrame);
    window.addEventListener('beforeunload', stopCamera);
  }

  async function startCamera() {
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: 'environment' }, audio: false
      });
      const video = $('hwVideo');
      video.srcObject = stream;
      await video.play();
      video.classList.remove('d-none');
      $('hwCamPlaceholder').classList.add('d-none');
      $('hwCamStart').classList.add('d-none');
      $('hwCamCapture').classList.remove('d-none');
    } catch (err) {
      alert('Could not access camera: ' + (err.message || err.name));
    }
  }

  function captureFrame() {
    const video  = $('hwVideo');
    const canvas = $('hwCanvas');
    canvas.width  = video.videoWidth;
    canvas.height = video.videoHeight;
    canvas.getContext('2d').drawImage(video, 0, 0);

    canvas.toBlob(function (blob) {
      const file = new File([blob], 'capture-' + Date.now() + '.png', { type: 'image/png' });
      addFiles([file]);
      stopCamera();
      switchTab('file');
    }, 'image/png');
  }

  function stopCamera() {
    if (stream) {
      stream.getTracks().forEach(function (t) { t.stop(); });
      stream = null;
    }
    const video = $('hwVideo');
    if (video) video.classList.add('d-none');
    const placeholder = $('hwCamPlaceholder');
    if (placeholder) placeholder.classList.remove('d-none');
    const start = $('hwCamStart');
    const cap   = $('hwCamCapture');
    if (start) start.classList.remove('d-none');
    if (cap)   cap.classList.add('d-none');
  }

  // ── Actions: analyze / submit ─────────────────────────────────────────────--
  function bindActions() {
    $('hwAnalyzeBtn').addEventListener('click', analyze);
    $('hwAddStepBtn').addEventListener('click', function () { addStepRow(stepCount() + 1, ''); });
    $('hwSubmitBtn').addEventListener('click', submit);
    $('hwCropConfirmBtn').addEventListener('click', confirmCrop);
    $('hwCropUseFullBtn').addEventListener('click', useFullPhoto);
  }

  function token() {
    const el = document.querySelector('input[name="__RequestVerificationToken"]');
    return el ? el.value : '';
  }

  async function analyze() {
    if (files.length === 0) return;

    showStage('loading');
    startProgress('AI is reading your solution…');

    const fd = new FormData();
    fd.append('assignmentId', assignmentId);
    fd.append('questionIndex', questionIndex);
    files.forEach(function (f) { fd.append('images', f); });
    fd.append('__RequestVerificationToken', token());

    try {
      const res  = await fetch('/Student/AnalyzeHandwritten', { method: 'POST', body: fd });
      const data = await res.json();
      if (!data.success) throw new Error(data.error || 'Analysis failed.');

      finishProgress();
      imagePaths = data.imagePaths || [];
      renderEditableSteps(data.steps || []);
      saveDraft();   // recognized steps cost an AI call — persist immediately
      showStage('edit');
    } catch (err) {
      stopProgress();
      alert(err.message);
      showStage('upload');
    }
  }

  async function submit() {
    const steps = collectSteps();
    if (steps.length === 0) { alert('Please add at least one step.'); return; }

    showStage('loading');
    startProgress('AI is checking your steps…');

    const fd = new FormData();
    fd.append('assignmentId', assignmentId);
    fd.append('questionIndex', questionIndex);
    fd.append('imagePathsJson', JSON.stringify(imagePaths));
    fd.append('stepsJson', JSON.stringify(steps));
    fd.append('__RequestVerificationToken', token());

    try {
      const res  = await fetch('/Student/SubmitHandwritten', { method: 'POST', body: fd });
      const data = await res.json();
      if (!data.success) throw new Error(data.error || 'Evaluation failed.');

      // Grading runs in the background now (several AI calls); poll until it's done.
      // The local progress ticker keeps the bar moving; the poll decides completion.
      pollGrading(data.jobId);
    } catch (err) {
      stopProgress();
      alert(err.message);
      showStage('edit');
    }
  }

  // Polls the background grading job for a photo/typed-step submission and shows
  // the result when it lands. Shared by the handwritten and (future) typed paths.
  function pollGrading(jobId) {
    if (!window.GradePoll) { stopProgress(); alert('Could not start grading.'); showStage('edit'); return; }
    GradePoll.start(jobId, {
      onDone: function (result) {
        finishProgress();
        clearDraft();   // evaluated and saved server-side — the draft is done
        renderResults(result);
        markAnswered(questionIndex);
        showStage('results');
      },
      onError: function (msg) {
        stopProgress();
        alert(msg);
        showStage('edit');
      }
    });
  }

  // ── Editable steps ────────────────────────────────────────────────────────--
  function renderEditableSteps(steps) {
    $('hwStepsList').innerHTML = '';
    if (steps.length === 0) { addStepRow(1, ''); return; }
    steps.forEach(function (s) { addStepRow(s.number, s.text); });
  }

  function addStepRow(number, text) {
    const row = document.createElement('div');
    row.className = 'hw-step-row';
    row.innerHTML =
      '<span class="hw-step-num"></span>' +
      '<input type="text" class="form-control hw-step-input" />' +
      '<button type="button" class="btn btn-sm btn-outline-danger hw-step-del"><i class="bx bx-trash"></i></button>';
    row.querySelector('.hw-step-num').textContent = number;
    row.querySelector('.hw-step-input').value = text || '';
    row.querySelector('.hw-step-del').addEventListener('click', function () {
      row.remove();
      renumberSteps();
    });
    $('hwStepsList').appendChild(row);
  }

  function renumberSteps() {
    document.querySelectorAll('#hwStepsList .hw-step-row').forEach(function (row, i) {
      row.querySelector('.hw-step-num').textContent = i + 1;
    });
  }

  function stepCount() {
    return document.querySelectorAll('#hwStepsList .hw-step-row').length;
  }

  function collectSteps() {
    const steps = [];
    document.querySelectorAll('#hwStepsList .hw-step-row').forEach(function (row, i) {
      const text = row.querySelector('.hw-step-input').value.trim();
      if (text) steps.push({ number: i + 1, text: text });
    });
    return steps;
  }

  // ── Results ───────────────────────────────────────────────────────────────--
  function renderResults(data) {
    const wrap = $('hwResults');
    const correct = (data.overall || '').toLowerCase() === 'correct';

    // Just this question's verdict — data.score is the whole assignment's running
    // percentage (recomputed after every question), not this one's, so it's left
    // out here; showing it next to a single question's result read as if it were
    // that question's own score.
    let html =
      '<div class="alert alert-' + (correct ? 'success' : 'warning') + ' py-2 px-3">' +
        '<strong>' + (correct ? '✅ Correct' : '❌ Not correct') + '</strong>' +
        (data.summary ? '<div class="small mt-1">' + escapeHtml(data.summary) + '</div>' : '') +
      '</div>';

    (data.steps || []).forEach(function (s) {
      const ok = (s.status || '').toLowerCase() === 'correct';
      // The reason a step is wrong shows on hover (a tooltip on the ✗ mark),
      // not as a permanently-open block of text under every wrong line.
      const mark = ok
        ? '✅'
        : '<span class="hw-step-mark"' +
            (s.explanation ? ' data-bs-toggle="tooltip" data-bs-placement="top" title="' + escapeAttr(s.explanation) + '"' : '') +
            '>❌</span>';
      html +=
        '<div class="hw-result-step ' + (ok ? 'correct' : 'incorrect') + '">' +
          '<div class="hw-step-text">' + mark + ' Step ' + s.number + ': ' + escapeHtml(s.text) + '</div>' +
        '</div>';
    });

    html += '<div class="d-flex gap-2 mt-2">' +
              '<button type="button" class="btn btn-sm btn-primary" data-bs-dismiss="modal">' +
                '<i class="bx bx-edit me-1"></i>Continue quiz</button>' +
              '<a href="/Student/AssignmentResult?assignmentId=' + assignmentId +
                '&resultId=' + data.resultId + '" class="btn btn-sm btn-outline-primary">' +
                '<i class="bx bx-list-check me-1"></i>View full result</a>' +
            '</div>';

    wrap.innerHTML = html;

    // Steps come back as LaTeX ($…$) — render them so they read like maths.
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

    initTooltips(wrap);
  }

  /** Bootstrap tooltips need explicit JS init — the data-bs-toggle attribute alone does nothing. */
  function initTooltips(wrap) {
    if (typeof bootstrap === 'undefined') return;
    wrap.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(function (el) {
      bootstrap.Tooltip.getOrCreateInstance(el);
    });
  }

  function markAnswered(idx) {
    const btn = document.querySelector('.question-nav .nav-btn[data-idx="' + idx + '"]');
    if (btn) btn.classList.add('answered');
  }

  function escapeHtml(str) {
    const div = document.createElement('div');
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
