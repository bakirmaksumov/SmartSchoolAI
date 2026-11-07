// Renders a clickable number line for NumberLine-type questions (see
// TakeAssignment.cshtml's ".nl-widget" blocks) and writes the picked value
// into the question's own <math-field>, dispatching a native 'input' event
// so take-assignment.js's existing answer-capture/grading pipeline picks it
// up unchanged — this widget only ever produces an input, never a grade.
(function () {
  var PAD_X = 30, CY = 45;

  function fmt(v) {
    var r = Math.round(v * 100) / 100;
    return Number.isInteger(r) ? String(r) : String(r);
  }

  function snap(value, min, max, step) {
    var grain = step / 4 || 1;
    var snapped = Math.round(value / grain) * grain;
    return Math.min(max, Math.max(min, snapped));
  }

  function render(svg, min, max, step, markValue) {
    var W = 480;
    var toX = function (v) { return PAD_X + (v - min) / (max - min) * (W - PAD_X * 2); };

    var parts = [];
    parts.push('<line x1="' + toX(min) + '" y1="' + CY + '" x2="' + toX(max) + '" y2="' + CY + '" stroke="#5c6079" stroke-width="1.8" />');

    var minor = step / 2 || 0.5;
    for (var v = min; v <= max + 1e-9; v += minor) {
      var isMajor = Math.abs(Math.round(v / step) * step - v) < 1e-9;
      var h = isMajor ? 10 : 5;
      var x = toX(v);
      parts.push('<line x1="' + x + '" y1="' + (CY - h) + '" x2="' + x + '" y2="' + (CY + h) + '" stroke="#5c6079" stroke-width="1.2" />');
      if (isMajor) parts.push('<text x="' + x + '" y="' + (CY + h + 14) + '" text-anchor="middle" font-size="11" fill="#5c6079">' + fmt(v) + '</text>');
    }

    if (markValue !== null && markValue !== undefined && !isNaN(markValue)) {
      var mx = toX(markValue);
      parts.push('<circle cx="' + mx + '" cy="' + CY + '" r="7" fill="#696cff" />');
      parts.push('<circle cx="' + mx + '" cy="' + CY + '" r="3" fill="#fff" />');
    }

    svg.innerHTML = parts.join('');
  }

  function init(widget) {
    var svg  = widget.querySelector('.nl-widget-svg');
    var min  = parseFloat(widget.dataset.min);
    var max  = parseFloat(widget.dataset.max);
    var step = parseFloat(widget.dataset.step) || 1;
    var qKey = widget.dataset.qkey;
    var mathField = document.querySelector('.answer-textarea[data-qkey="' + qKey + '"]');
    if (!svg || !mathField || isNaN(min) || isNaN(max) || max <= min) return;

    var current = parseFloat(mathField.value);
    render(svg, min, max, step, isNaN(current) ? null : current);

    svg.addEventListener('click', function (e) {
      var rect = svg.getBoundingClientRect();
      var scaleX = 480 / rect.width;
      var px = (e.clientX - rect.left) * scaleX;
      var value = min + (px - PAD_X) / (480 - PAD_X * 2) * (max - min);
      value = snap(value, min, max, step);

      mathField.value = fmt(value);
      mathField.dispatchEvent(new Event('input', { bubbles: true }));

      render(svg, min, max, step, value);
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.nl-widget').forEach(init);
  });
})();
