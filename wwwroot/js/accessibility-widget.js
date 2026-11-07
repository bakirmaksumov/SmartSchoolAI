// "Read aloud" (Web Speech API, fully client-side) and "Translate" (calls
// /Student/Translate, AI-backed) controls shown per-question on TakeAssignment
// when the class has voice/translation assistance turned on.
(function () {
  function csrfToken() {
    return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
  }

  // ── Read aloud ──────────────────────────────────────────────────────────
  function bindReadAloud() {
    if (!('speechSynthesis' in window)) return;

    document.querySelectorAll('.read-aloud-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var target = document.getElementById(btn.dataset.target);
        if (!target) return;

        if (window.speechSynthesis.speaking) {
          window.speechSynthesis.cancel();
          resetReadAloudButtons();
          return;
        }

        var utterance = new SpeechSynthesisUtterance(target.textContent.trim());
        utterance.onend = function () { resetReadAloudButtons(); };
        utterance.onerror = function () { resetReadAloudButtons(); };

        resetReadAloudButtons();
        btn.innerHTML = '<i class="bx bx-stop-circle me-1"></i> Stop';
        window.speechSynthesis.speak(utterance);
      });
    });
  }

  function resetReadAloudButtons() {
    document.querySelectorAll('.read-aloud-btn').forEach(function (b) {
      b.innerHTML = '<i class="bx bx-volume-full me-1"></i> Read aloud';
    });
  }

  // ── Translate ───────────────────────────────────────────────────────────
  function bindTranslate() {
    document.querySelectorAll('.translate-btn').forEach(function (btn) {
      btn.addEventListener('click', async function () {
        var source = document.getElementById(btn.dataset.source);
        var target = document.getElementById(btn.dataset.target);
        if (!source || !target) return;

        target.classList.remove('d-none');
        target.innerHTML = '<span class="text-muted small"><i class="bx bx-loader-alt bx-spin me-1"></i>Translating…</span>';

        var fd = new FormData();
        fd.append('__RequestVerificationToken', csrfToken());
        fd.append('assignmentId', btn.dataset.assignment);
        fd.append('text', source.textContent.trim());
        fd.append('languageCode', btn.dataset.lang);

        try {
          var res = await fetch('/Student/Translate', { method: 'POST', body: fd });
          var data = await res.json();
          if (data.success) {
            target.textContent = data.translated;
          } else {
            target.innerHTML = '<span class="text-danger small">' + (data.error || 'Could not translate.') + '</span>';
          }
        } catch (e) {
          target.innerHTML = '<span class="text-danger small">Could not translate. Please try again.</span>';
        }
      });
    });

    document.querySelectorAll('.translate-clear-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var target = document.getElementById(btn.dataset.target);
        if (target) target.classList.add('d-none');
      });
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    bindReadAloud();
    bindTranslate();
  });
})();
