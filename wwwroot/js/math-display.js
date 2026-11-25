// Shows student answers, recognised steps and AI text as readable maths, never as raw LaTeX.
// Every element with class "math-text" is passed through MathText.renderInto (math-text.js): simple expressions become
// readable text ("f⁻¹(x) = −2x/√(1 + x²)"), notation that cannot be written as plain text (cases, integrals…) is
// typeset by KaTeX. The element's text is read once from the server-rendered (HTML-encoded) page, so nothing the
// student or the AI wrote is ever inserted as markup.
(function () {
  'use strict';

  function apply(root) {
    if (!window.MathText) return;
    (root || document).querySelectorAll('.math-text:not([data-math-done])').forEach(function (el) {
      var raw = (el.textContent || '').trim();
      el.setAttribute('data-math-done', '1');
      if (!raw || !/[$\\^_{}]/.test(raw)) return;          // plain words or numbers: nothing to convert
      el.setAttribute('data-latex-source', raw);            // the original stays available (copy, tests, teacher)
      // Multi-line text (working, AI feedback) keeps its lines and lists; everything else stays inside its line.
      if (el.classList.contains('math-block')) MathText.renderInto(el, raw);
      else MathText.inlineInto(el, raw);
      if (MathText.typeset) MathText.typeset(el);
    });
  }

  window.MathDisplay = { apply: apply };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', function () { apply(); });
  else apply();
  // KaTeX may arrive after this script: typeset what is left once everything has loaded.
  window.addEventListener('load', function () {
    if (!window.MathText || !MathText.typeset) return;
    document.querySelectorAll('.math-text[data-latex-source]').forEach(function (el) { MathText.typeset(el); });
  });
})();
