// Renders a clickable dot grid for GeometryDrawing-type questions (see
// TakeAssignment.cshtml's ".geo-widget" blocks). The student places points,
// segments, lines, rays or circles snapped to the grid; the resulting
// description is written as plain text into the question's own <math-field>
// so take-assignment.js's existing answer-capture/grading pipeline picks it
// up unchanged. Starts blank — the teacher's own elements (the answer key)
// never reach this widget.
(function () {
  var SIZE = 480;
  var POINTS_NEEDED = { point: 1, segment: 2, line: 2, ray: 2, circle: 2 };

  function describe(el) {
    if (el.type === 'point')   return 'point(' + el.pts[0][0] + ',' + el.pts[0][1] + ')';
    if (el.type === 'segment') return 'segment(' + el.pts[0][0] + ',' + el.pts[0][1] + ')-(' + el.pts[1][0] + ',' + el.pts[1][1] + ')';
    if (el.type === 'line')    return 'line through (' + el.pts[0][0] + ',' + el.pts[0][1] + ') and (' + el.pts[1][0] + ',' + el.pts[1][1] + ')';
    if (el.type === 'ray')     return 'ray from (' + el.pts[0][0] + ',' + el.pts[0][1] + ') through (' + el.pts[1][0] + ',' + el.pts[1][1] + ')';
    if (el.type === 'circle')  return 'circle center(' + el.pts[0][0] + ',' + el.pts[0][1] + ') through(' + el.pts[1][0] + ',' + el.pts[1][1] + ')';
    return '';
  }

  function cellToPx(col, row, gridSize) { return [col * gridSize, row * gridSize]; }
  function pxToCell(px, py, gridSize) { return [Math.round(px / gridSize), Math.round(py / gridSize)]; }

  function dot(parts, p, col) {
    parts.push('<circle cx="' + p[0] + '" cy="' + p[1] + '" r="5" fill="' + col + '" />');
    parts.push('<circle cx="' + p[0] + '" cy="' + p[1] + '" r="2.5" fill="#fff" />');
  }

  function drawElement(parts, el, gridSize) {
    var col = '#696cff';
    if (el.type === 'point') {
      dot(parts, cellToPx(el.pts[0][0], el.pts[0][1], gridSize), col);
    } else if (el.type === 'segment') {
      var a = cellToPx(el.pts[0][0], el.pts[0][1], gridSize), b = cellToPx(el.pts[1][0], el.pts[1][1], gridSize);
      parts.push('<line x1="' + a[0] + '" y1="' + a[1] + '" x2="' + b[0] + '" y2="' + b[1] + '" stroke="' + col + '" stroke-width="2" />');
      dot(parts, a, col); dot(parts, b, col);
    } else if (el.type === 'line') {
      var p1 = cellToPx(el.pts[0][0], el.pts[0][1], gridSize), p2 = cellToPx(el.pts[1][0], el.pts[1][1], gridSize);
      var dx = p2[0] - p1[0], dy = p2[1] - p1[1], ext = 40;
      parts.push('<line x1="' + (p1[0] - dx * ext) + '" y1="' + (p1[1] - dy * ext) + '" x2="' + (p1[0] + dx * ext) + '" y2="' + (p1[1] + dy * ext) + '" stroke="' + col + '" stroke-width="2" />');
      dot(parts, p1, col); dot(parts, p2, col);
    } else if (el.type === 'ray') {
      var r1 = cellToPx(el.pts[0][0], el.pts[0][1], gridSize), r2 = cellToPx(el.pts[1][0], el.pts[1][1], gridSize);
      var rdx = r2[0] - r1[0], rdy = r2[1] - r1[1], rext = 40;
      parts.push('<line x1="' + r1[0] + '" y1="' + r1[1] + '" x2="' + (r1[0] + rdx * rext) + '" y2="' + (r1[1] + rdy * rext) + '" stroke="' + col + '" stroke-width="2" />');
      dot(parts, r1, col);
    } else if (el.type === 'circle') {
      var c = cellToPx(el.pts[0][0], el.pts[0][1], gridSize), rp = cellToPx(el.pts[1][0], el.pts[1][1], gridSize);
      var r = Math.hypot(rp[0] - c[0], rp[1] - c[1]);
      parts.push('<circle cx="' + c[0] + '" cy="' + c[1] + '" r="' + r + '" stroke="' + col + '" stroke-width="2" fill="none" />');
      dot(parts, c, col);
    }
  }

  function render(svg, gridSize, elements, pending) {
    var parts = [];
    var cols = Math.floor(SIZE / gridSize), rows = Math.floor(SIZE / gridSize);
    for (var c = 0; c <= cols; c++) {
      var x = c * gridSize;
      parts.push('<line x1="' + x + '" y1="0" x2="' + x + '" y2="' + SIZE + '" stroke="#dce0f5" stroke-width="1" />');
    }
    for (var r = 0; r <= rows; r++) {
      var y = r * gridSize;
      parts.push('<line x1="0" y1="' + y + '" x2="' + SIZE + '" y2="' + y + '" stroke="#dce0f5" stroke-width="1" />');
    }
    parts.push('<rect x="0" y="0" width="' + SIZE + '" height="' + SIZE + '" fill="none" stroke="#c2c7e8" stroke-width="1.5" />');

    elements.forEach(function (el) { drawElement(parts, el, gridSize); });
    pending.forEach(function (cell) {
      var p = cellToPx(cell[0], cell[1], gridSize);
      parts.push('<circle cx="' + p[0] + '" cy="' + p[1] + '" r="4" fill="rgba(105,108,255,0.45)" />');
    });

    svg.innerHTML = parts.join('');
  }

  // Reconstructs elements[] from describe()'s own output — take-assignment.js
  // restores a saved draft's text into the math-field before this widget inits,
  // but only this parser turns that text back into something to redraw/append to.
  function parseAnswer(text) {
    var out = [];
    (text || '').split(';').forEach(function (part) {
      part = part.trim();
      if (!part) return;
      var m;
      if ((m = part.match(/^point\(([^,]+),([^)]+)\)$/))) {
        out.push({ type: 'point', pts: [[parseFloat(m[1]), parseFloat(m[2])]] });
      } else if ((m = part.match(/^segment\(([^,]+),([^)]+)\)-\(([^,]+),([^)]+)\)$/))) {
        out.push({ type: 'segment', pts: [[parseFloat(m[1]), parseFloat(m[2])], [parseFloat(m[3]), parseFloat(m[4])]] });
      } else if ((m = part.match(/^line through \(([^,]+),([^)]+)\) and \(([^,]+),([^)]+)\)$/))) {
        out.push({ type: 'line', pts: [[parseFloat(m[1]), parseFloat(m[2])], [parseFloat(m[3]), parseFloat(m[4])]] });
      } else if ((m = part.match(/^ray from \(([^,]+),([^)]+)\) through \(([^,]+),([^)]+)\)$/))) {
        out.push({ type: 'ray', pts: [[parseFloat(m[1]), parseFloat(m[2])], [parseFloat(m[3]), parseFloat(m[4])]] });
      } else if ((m = part.match(/^circle center\(([^,]+),([^)]+)\) through\(([^,]+),([^)]+)\)$/))) {
        out.push({ type: 'circle', pts: [[parseFloat(m[1]), parseFloat(m[2])], [parseFloat(m[3]), parseFloat(m[4])]] });
      }
    });
    return out;
  }

  function init(widget) {
    var svg = widget.querySelector('.geo-widget-svg');
    var gridSize = parseInt(widget.dataset.gridsize, 10) || 20;
    var qKey = widget.dataset.qkey;
    var mathField = document.querySelector('.answer-textarea[data-qkey="' + qKey + '"]');
    if (!svg || !mathField) return;

    var elements = parseAnswer(mathField.value);
    var pending = [];
    var mode = 'point';

    function redraw() { render(svg, gridSize, elements, pending); }
    function serialise() {
      mathField.value = elements.map(describe).join('; ');
      mathField.dispatchEvent(new Event('input', { bubbles: true }));
    }
    redraw();

    widget.querySelectorAll('.geo-mode-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        mode = btn.dataset.mode;
        widget.querySelectorAll('.geo-mode-btn').forEach(function (b) { b.classList.remove('active'); });
        btn.classList.add('active');
        pending = [];
        redraw();
      });
    });

    var undoBtn = widget.querySelector('.geo-undo-btn');
    if (undoBtn) undoBtn.addEventListener('click', function () {
      elements.pop();
      serialise();
      redraw();
    });

    svg.addEventListener('click', function (e) {
      var rect = svg.getBoundingClientRect();
      var scale = SIZE / rect.width;
      var cx = (e.clientX - rect.left) * scale;
      var cy = (e.clientY - rect.top) * scale;
      var cell = pxToCell(cx, cy, gridSize);

      pending.push(cell);
      var needed = POINTS_NEEDED[mode] || 1;
      if (pending.length >= needed) {
        elements.push({ type: mode, pts: pending.slice() });
        pending = [];
        serialise();
      }
      redraw();
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.geo-widget').forEach(init);
  });
})();
