// Renders a clickable coordinate plane for Graphing-type questions (see
// TakeAssignment.cshtml's ".gph-widget" blocks). The student places points,
// segments, lines or circles; the resulting description is written as plain
// text into the question's own <math-field> so take-assignment.js's existing
// answer-capture/grading pipeline picks it up unchanged. Starts blank — the
// teacher's own elements (the answer key) never reach this widget.
(function () {
  var SIZE = 480;
  var POINTS_NEEDED = { point: 1, segment: 2, linear: 2, circle: 2 };

  function fmt(v) {
    var r = Math.round(v * 100) / 100;
    return Number.isInteger(r) ? String(r) : String(r);
  }

  function describe(el) {
    if (el.type === 'point')   return 'point(' + fmt(el.pts[0][0]) + ',' + fmt(el.pts[0][1]) + ')';
    if (el.type === 'segment') return 'segment(' + fmt(el.pts[0][0]) + ',' + fmt(el.pts[0][1]) + ')-(' + fmt(el.pts[1][0]) + ',' + fmt(el.pts[1][1]) + ')';
    if (el.type === 'linear')  return 'line through (' + fmt(el.pts[0][0]) + ',' + fmt(el.pts[0][1]) + ') and (' + fmt(el.pts[1][0]) + ',' + fmt(el.pts[1][1]) + ')';
    if (el.type === 'circle')  return 'circle center(' + fmt(el.pts[0][0]) + ',' + fmt(el.pts[0][1]) + ') r=' + fmt(el.r);
    return '';
  }

  function makeGeom(xMin, xMax, yMin, yMax) {
    return {
      toCanvas: function (gx, gy) {
        return [(gx - xMin) / (xMax - xMin) * SIZE, (1 - (gy - yMin) / (yMax - yMin)) * SIZE];
      },
      toGraph: function (cx, cy) {
        return [cx / SIZE * (xMax - xMin) + xMin, (1 - cy / SIZE) * (yMax - yMin) + yMin];
      },
      snap: function (cx, cy) {
        var g = this.toGraph(cx, cy);
        return [Math.round(g[0] * 2) / 2, Math.round(g[1] * 2) / 2];
      }
    };
  }

  function render(svg, geom, xMin, xMax, yMin, yMax, elements, pending) {
    var parts = [];

    for (var x = Math.ceil(xMin); x <= xMax; x++) {
      var px = geom.toCanvas(x, 0)[0];
      parts.push('<line x1="' + px + '" y1="0" x2="' + px + '" y2="' + SIZE + '" stroke="#e0e4f0" stroke-width="1" />');
    }
    for (var y = Math.ceil(yMin); y <= yMax; y++) {
      var py = geom.toCanvas(0, y)[1];
      parts.push('<line x1="0" y1="' + py + '" x2="' + SIZE + '" y2="' + py + '" stroke="#e0e4f0" stroke-width="1" />');
    }

    var ax0 = geom.toCanvas(0, yMin), ax1 = geom.toCanvas(0, yMax);
    var bx0 = geom.toCanvas(xMin, 0), bx1 = geom.toCanvas(xMax, 0);
    parts.push('<line x1="' + ax0[0] + '" y1="' + ax0[1] + '" x2="' + ax1[0] + '" y2="' + ax1[1] + '" stroke="#5c6079" stroke-width="1.8" />');
    parts.push('<line x1="' + bx0[0] + '" y1="' + bx0[1] + '" x2="' + bx1[0] + '" y2="' + bx1[1] + '" stroke="#5c6079" stroke-width="1.8" />');

    for (var xi = Math.ceil(xMin); xi <= xMax; xi++) {
      if (xi === 0) continue;
      var p = geom.toCanvas(xi, 0);
      parts.push('<text x="' + p[0] + '" y="' + (p[1] + 14) + '" text-anchor="middle" font-size="11" fill="#5c6079">' + xi + '</text>');
    }
    for (var yi = Math.ceil(yMin); yi <= yMax; yi++) {
      if (yi === 0) continue;
      var p2 = geom.toCanvas(0, yi);
      parts.push('<text x="' + (p2[0] - 6) + '" y="' + (p2[1] + 4) + '" text-anchor="end" font-size="11" fill="#5c6079">' + yi + '</text>');
    }

    elements.forEach(function (el) { drawElement(parts, geom, xMin, xMax, el); });
    pending.forEach(function (pt) {
      var c = geom.toCanvas(pt[0], pt[1]);
      parts.push('<circle cx="' + c[0] + '" cy="' + c[1] + '" r="5" fill="rgba(105,108,255,0.5)" />');
    });

    svg.innerHTML = parts.join('');
  }

  function dot(parts, c, col) {
    parts.push('<circle cx="' + c[0] + '" cy="' + c[1] + '" r="5" fill="' + col + '" />');
    parts.push('<circle cx="' + c[0] + '" cy="' + c[1] + '" r="2.5" fill="#fff" />');
  }

  function drawElement(parts, geom, xMin, xMax, el) {
    var col = '#696cff';
    if (el.type === 'point') {
      dot(parts, geom.toCanvas(el.pts[0][0], el.pts[0][1]), col);
    } else if (el.type === 'segment') {
      var a = geom.toCanvas(el.pts[0][0], el.pts[0][1]), b = geom.toCanvas(el.pts[1][0], el.pts[1][1]);
      parts.push('<line x1="' + a[0] + '" y1="' + a[1] + '" x2="' + b[0] + '" y2="' + b[1] + '" stroke="' + col + '" stroke-width="2" />');
      dot(parts, a, col); dot(parts, b, col);
    } else if (el.type === 'linear') {
      var p1 = geom.toCanvas(el.pts[0][0], el.pts[0][1]), p2 = geom.toCanvas(el.pts[1][0], el.pts[1][1]);
      var dx = p2[0] - p1[0], dy = p2[1] - p1[1], ext = 30;
      parts.push('<line x1="' + (p1[0] - dx * ext) + '" y1="' + (p1[1] - dy * ext) + '" x2="' + (p1[0] + dx * ext) + '" y2="' + (p1[1] + dy * ext) + '" stroke="' + col + '" stroke-width="2" />');
      dot(parts, p1, col); dot(parts, p2, col);
    } else if (el.type === 'circle') {
      var c = geom.toCanvas(el.pts[0][0], el.pts[0][1]);
      var rc = geom.toCanvas(el.pts[0][0] + el.r, el.pts[0][1]);
      var r = Math.abs(rc[0] - c[0]);
      parts.push('<circle cx="' + c[0] + '" cy="' + c[1] + '" r="' + r + '" stroke="' + col + '" stroke-width="2" fill="none" />');
      dot(parts, c, col);
    }
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
        out.push({ type: 'linear', pts: [[parseFloat(m[1]), parseFloat(m[2])], [parseFloat(m[3]), parseFloat(m[4])]] });
      } else if ((m = part.match(/^circle center\(([^,]+),([^)]+)\) r=([^\s]+)$/))) {
        out.push({ type: 'circle', pts: [[parseFloat(m[1]), parseFloat(m[2])]], r: parseFloat(m[3]) });
      }
    });
    return out;
  }

  function init(widget) {
    var svg = widget.querySelector('.gph-widget-svg');
    var xMin = parseFloat(widget.dataset.xmin), xMax = parseFloat(widget.dataset.xmax);
    var yMin = parseFloat(widget.dataset.ymin), yMax = parseFloat(widget.dataset.ymax);
    var qKey = widget.dataset.qkey;
    var mathField = document.querySelector('.answer-textarea[data-qkey="' + qKey + '"]');
    if (!svg || !mathField || isNaN(xMin) || isNaN(xMax) || isNaN(yMin) || isNaN(yMax)) return;

    var geom = makeGeom(xMin, xMax, yMin, yMax);
    var elements = parseAnswer(mathField.value);
    var pending = [];
    var mode = 'point';

    function redraw() { render(svg, geom, xMin, xMax, yMin, yMax, elements, pending); }
    function serialise() {
      mathField.value = elements.map(describe).join('; ');
      mathField.dispatchEvent(new Event('input', { bubbles: true }));
    }
    redraw();

    widget.querySelectorAll('.gph-mode-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        mode = btn.dataset.mode;
        widget.querySelectorAll('.gph-mode-btn').forEach(function (b) { b.classList.remove('active'); });
        btn.classList.add('active');
        pending = [];
        redraw();
      });
    });

    var undoBtn = widget.querySelector('.gph-undo-btn');
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
      var pt = geom.snap(cx, cy);

      pending.push(pt);
      var needed = POINTS_NEEDED[mode] || 1;
      if (pending.length >= needed) {
        if (mode === 'circle') {
          var r = Math.hypot(pending[1][0] - pending[0][0], pending[1][1] - pending[0][1]);
          elements.push({ type: 'circle', pts: [pending[0]], r: r });
        } else {
          elements.push({ type: mode, pts: pending.slice() });
        }
        pending = [];
        serialise();
      }
      redraw();
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.gph-widget').forEach(init);
  });
})();
