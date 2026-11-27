// Live "how students see it" preview for fields that hold maths as LaTeX in $…$ (question text, shared stem, answer
// key, marking criteria). Each element with data-preview-of="<selector>" shows the source field's text through
// MathText.renderInto (math-text.js): simple expressions as readable text, the rest typeset by KaTeX. It builds DOM
// nodes only, so nothing typed is inserted as markup. The preview stays hidden while the text has no maths.
(function () {
  'use strict';

  function sourceText(src) {
    if (src.isContentEditable) {
      // The rich editor's own equation chips, tables and images already show as they will look.
      return src.querySelector('.eq-chip, table, img') ? null : src.textContent;
    }
    return src.value;
  }

  function update(preview, src) {
    var text = sourceText(src);
    var body = preview.querySelector('[data-preview-body]') || preview;
    if (text == null || !/[$\\^_]/.test(text)) { preview.hidden = true; return; }
    preview.hidden = false;
    window.MathText.renderInto(body, text.trim());
    if (window.MathText.typeset) window.MathText.typeset(body);
  }

  function init() {
    if (!window.MathText) return;
    document.querySelectorAll('[data-preview-of]').forEach(function (preview) {
      var src = document.querySelector(preview.getAttribute('data-preview-of'));
      if (!src) return;
      var timer;
      src.addEventListener('input', function () {
        clearTimeout(timer);
        timer = setTimeout(function () { update(preview, src); }, 250);
      });
      update(preview, src);
    });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
  // KaTeX may load after this script: typeset again once everything is there.
  window.addEventListener('load', function () {
    document.querySelectorAll('[data-preview-of]:not([hidden])').forEach(function (preview) {
      var src = document.querySelector(preview.getAttribute('data-preview-of'));
      if (src) update(preview, src);
    });
  });
})();
