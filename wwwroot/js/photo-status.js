// Per-question photo status on the assignment page: what is uploading, what the server has saved, what failed.
// The saved state comes from the server (rendered into the page) or from a confirmed save, never from the
// browser alone, so "Photo saved to question 3" always means the server has it.
(function () {
  'use strict';

  var style = document.createElement('style');
  style.textContent =
    '.photo-status{display:flex;flex-wrap:wrap;align-items:center;gap:.5rem;padding:.5rem .75rem;border-radius:.5rem;background:var(--bs-gray-100,#f5f5f9)}' +
    '.photo-status[data-state=saved]{background:rgba(113,221,55,.12)}.photo-status[data-state=failed]{background:rgba(255,62,29,.1);color:#b4250f}' +
    '.photo-thumbs{display:inline-flex;gap:.35rem}.photo-thumbs img{height:56px;width:auto;border-radius:.35rem;border:1px solid var(--bs-border-color,#ddd)}';
  document.head.appendChild(style);

  var active = {};          // question index -> true while a photo is on its way
  var saved = {};           // question index -> [web paths] confirmed by the server

  function box(qi) { return document.querySelector('.photo-status[data-qindex="' + qi + '"]'); }
  function label(qi) { var b = box(qi); return b ? b.dataset.label : String(Number(qi) + 1); }
  function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : String(s); return d.innerHTML; }

  function render(qi, html, state) {
    var b = box(qi);
    if (!b) return;
    b.innerHTML = html;
    b.dataset.state = state;
    b.classList.toggle('d-none', !html);
  }

  function thumbs(paths) {
    return '<span class="photo-thumbs">' + (paths || []).map(function (p, i) {
      return '<a href="' + esc(p) + '" target="_blank" rel="noopener" title="Open the saved photo">' +
             '<img src="' + esc(p) + '" alt="Saved photo' + (paths.length > 1 ? ' page ' + (i + 1) : '') + '" loading="lazy"></a>';
    }).join('') + '</span>';
  }

  function changed() {
    document.dispatchEvent(new CustomEvent('photostatus:change', { detail: { active: api.activeCount(), saved: api.savedCount() } }));
  }

  var api = {
    activeCount: function () { return Object.keys(active).length; },
    savedCount: function () { return Object.keys(saved).length; },
    hasSaved: function (qi) { return !!saved[qi]; },

    uploading: function (qi) {
      active[qi] = true;
      render(qi, '<span class="spinner-border spinner-border-sm me-2" aria-hidden="true"></span>Uploading…', 'uploading');
      changed();
    },
    uploaded: function (qi) {
      // The photo reached the server and was read; it is attached to the question once the steps are sent.
      delete active[qi];
      render(qi, '<i class="bx bx-cloud-upload me-1" aria-hidden="true"></i>Photo uploaded — check the steps and press Submit in the photo window to save it to question ' + esc(label(qi)) + '.', 'uploaded');
      changed();
    },
    saving: function (qi) {
      active[qi] = true;
      render(qi, '<span class="spinner-border spinner-border-sm me-2" aria-hidden="true"></span>Saving the photo to question ' + esc(label(qi)) + '…', 'saving');
      changed();
    },
    saved: function (qi, paths, note) {
      delete active[qi];
      saved[qi] = paths || [];
      render(qi, thumbs(paths) + '<span class="photo-saved-text"><i class="bx bx-check-circle me-1" aria-hidden="true"></i>Photo saved to question ' +
             esc(label(qi)) + (note ? ' <span class="text-muted">· ' + esc(note) + '</span>' : '') + '</span>', 'saved');
      if (window.QuizPills) QuizPills.setAnswered(Number(qi), true);
      changed();
    },
    failed: function (qi, message) {
      delete active[qi];
      render(qi, '<i class="bx bx-error-circle me-1" aria-hidden="true"></i>' + esc(message || 'The photo was not uploaded.') +
             (saved[qi] ? ' Your earlier photo is still saved.' : '') +
             ' <button type="button" class="btn btn-xs btn-outline-danger ms-1 photo-retry" data-qindex="' + esc(qi) + '">Try again</button>', 'failed');
      changed();
    }
  };
  window.PhotoStatus = api;

  // "Try again" opens the photo window for that question again.
  document.addEventListener('click', function (e) {
    var b = e.target.closest('.photo-retry');
    if (!b) return;
    var open = document.querySelector('[data-hw-open][data-qindex="' + b.dataset.qindex + '"]');
    if (open) open.click();
  });

  // Photos the server already holds for this attempt (another device, an earlier visit, a reload).
  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.photo-status[data-saved]').forEach(function (b) {
      var paths = [];
      try { paths = JSON.parse(b.dataset.saved || '[]'); } catch (e) { paths = []; }
      if (paths.length) api.saved(b.dataset.qindex, paths, b.dataset.note || '');
    });
  });
})();
