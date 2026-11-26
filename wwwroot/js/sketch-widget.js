// Sketch pad for questions the teacher marked "student answers by drawing" (see the
// ".sketch-widget" blocks in TakeAssignment.cshtml). The student draws with pen or
// straight-line tools over the question's diagram (or a plain grid when there is none).
//
// Strokes are kept as vectors so the eraser and undo never touch the diagram underneath.
// Two values leave the widget:
//   - the strokes (JSON) go into the quiz's answers{} via QuizAnswers, so the draft saved
//     in localStorage brings the drawing back after a reload;
//   - the finished picture (diagram + strokes, PNG data URL) goes into the hidden
//     "sketchpng_{i}" field, which the server saves and marks on submit.
(function () {
  'use strict';

  var WIDTH = 800;            // internal canvas width; the canvas is scaled down with CSS
  var GRID  = 25;
  var ERASE_RADIUS = 12;
  var MAX_HISTORY = 50;

  function distToSegment(p, a, b) {
    var dx = b[0] - a[0], dy = b[1] - a[1];
    var len2 = dx * dx + dy * dy;
    var t = len2 === 0 ? 0 : Math.max(0, Math.min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / len2));
    var x = a[0] + t * dx, y = a[1] + t * dy;
    return Math.hypot(p[0] - x, p[1] - y);
  }

  function hits(stroke, p) {
    var pts = stroke.p;
    if (pts.length === 1) return Math.hypot(p[0] - pts[0][0], p[1] - pts[0][1]) <= ERASE_RADIUS;
    for (var i = 1; i < pts.length; i++)
      if (distToSegment(p, pts[i - 1], pts[i]) <= ERASE_RADIUS + stroke.w / 2) return true;
    return false;
  }

  function parseStrokes(json) {
    try {
      var s = JSON.parse(json || '[]');
      return Array.isArray(s) ? s.filter(function (x) { return x && Array.isArray(x.p) && x.p.length; }) : [];
    } catch (e) { return []; }
  }

  function init(widget) {
    var idx    = widget.dataset.qindex;
    var canvas = widget.querySelector('.sketch-canvas');
    var pngOut = document.getElementById('sketchpng_' + idx);
    if (!canvas || !pngOut) return;

    var ctx     = canvas.getContext('2d');
    var bg      = null;                    // loaded diagram image, or null for the grid
    var strokes = parseStrokes(window.QuizAnswers ? QuizAnswers.get('sketch_' + idx) : '');
    var history = [];
    var tool    = 'pen';
    var color   = '#1d4ed8';
    var width   = 3;
    var current = null;                    // stroke being drawn
    var erasedThisDrag = false;

    canvas.width  = WIDTH;
    canvas.height = 500;

    function drawBackground() {
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      if (bg) {
        ctx.drawImage(bg, 0, 0, canvas.width, canvas.height);
        return;
      }
      ctx.strokeStyle = '#e5e7eb';
      ctx.lineWidth = 1;
      for (var x = GRID; x < canvas.width; x += GRID) { ctx.beginPath(); ctx.moveTo(x + .5, 0); ctx.lineTo(x + .5, canvas.height); ctx.stroke(); }
      for (var y = GRID; y < canvas.height; y += GRID) { ctx.beginPath(); ctx.moveTo(0, y + .5); ctx.lineTo(canvas.width, y + .5); ctx.stroke(); }
    }

    function drawStroke(s) {
      ctx.strokeStyle = s.c;
      ctx.fillStyle   = s.c;
      ctx.lineWidth   = s.w;
      ctx.lineCap     = 'round';
      ctx.lineJoin    = 'round';
      if (s.p.length === 1) {
        ctx.beginPath(); ctx.arc(s.p[0][0], s.p[0][1], s.w / 2, 0, Math.PI * 2); ctx.fill();
        return;
      }
      ctx.beginPath();
      ctx.moveTo(s.p[0][0], s.p[0][1]);
      for (var i = 1; i < s.p.length; i++) ctx.lineTo(s.p[i][0], s.p[i][1]);
      ctx.stroke();
    }

    function redraw() {
      drawBackground();
      strokes.forEach(drawStroke);
      if (current) drawStroke(current);
    }

    // Saves the strokes (for the draft) and the finished picture (for the submission).
    // On first load the strokes came from the draft, so only the picture is rebuilt.
    function commit(initial) {
      if (!initial && window.QuizAnswers) QuizAnswers.set('sketch_' + idx, strokes.length ? JSON.stringify(strokes) : '');
      if (!strokes.length) {
        pngOut.value = '';
      } else {
        try {
          pngOut.value = canvas.toDataURL('image/png');
        } catch (e) {
          // A diagram from another site taints the canvas: send the strokes on white instead.
          var savedBg = bg; bg = null; redraw();
          pngOut.value = canvas.toDataURL('image/png');
          bg = savedBg; redraw();
        }
      }
      if (window.QuizPills) QuizPills.setAnswered(parseInt(idx, 10), strokes.length > 0);
      widget.querySelector('.sketch-undo').disabled  = history.length === 0;
      widget.querySelector('.sketch-clear').disabled = strokes.length === 0;
    }

    function remember() {
      history.push(JSON.stringify(strokes));
      if (history.length > MAX_HISTORY) history.shift();
    }

    function pointFrom(e) {
      var rect = canvas.getBoundingClientRect();
      return [
        Math.round((e.clientX - rect.left) * canvas.width / rect.width),
        Math.round((e.clientY - rect.top) * canvas.height / rect.height)
      ];
    }

    function eraseAt(p) {
      var before = strokes.length;
      var kept = strokes.filter(function (s) { return !hits(s, p); });
      if (kept.length === before) return;
      if (!erasedThisDrag) { remember(); erasedThisDrag = true; }
      strokes = kept;
      redraw();
    }

    canvas.addEventListener('pointerdown', function (e) {
      if (e.button !== undefined && e.button > 0) return;
      e.preventDefault();
      canvas.setPointerCapture(e.pointerId);
      var p = pointFrom(e);
      if (tool === 'eraser') { erasedThisDrag = false; eraseAt(p); return; }
      current = { t: tool, c: color, w: width, p: [p] };
      redraw();
    });

    canvas.addEventListener('pointermove', function (e) {
      if (!canvas.hasPointerCapture(e.pointerId)) return;
      var p = pointFrom(e);
      if (tool === 'eraser') { eraseAt(p); return; }
      if (!current) return;
      if (current.t === 'line') {
        current.p = [current.p[0], p];
      } else {
        var last = current.p[current.p.length - 1];
        if (Math.hypot(p[0] - last[0], p[1] - last[1]) < 2) return;
        current.p.push(p);
      }
      redraw();
    });

    function finish(e) {
      if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
      if (tool === 'eraser') { if (erasedThisDrag) commit(); erasedThisDrag = false; return; }
      if (!current) return;
      remember();
      strokes.push(current);
      current = null;
      redraw();
      commit();
    }
    canvas.addEventListener('pointerup', finish);
    canvas.addEventListener('pointercancel', finish);

    widget.querySelectorAll('.sketch-tool').forEach(function (btn) {
      btn.addEventListener('click', function () {
        tool = btn.dataset.tool;
        widget.querySelectorAll('.sketch-tool').forEach(function (b) { b.classList.toggle('active', b === btn); });
        canvas.style.cursor = tool === 'eraser' ? 'cell' : 'crosshair';
      });
    });

    widget.querySelectorAll('.sketch-color').forEach(function (btn) {
      btn.addEventListener('click', function () {
        color = btn.dataset.color;
        widget.querySelectorAll('.sketch-color').forEach(function (b) { b.classList.toggle('active', b === btn); });
        if (tool === 'eraser') widget.querySelector('.sketch-tool[data-tool="pen"]').click();
      });
    });

    var widthSel = widget.querySelector('.sketch-width');
    if (widthSel) widthSel.addEventListener('change', function () { width = parseInt(widthSel.value, 10) || 3; });

    widget.querySelector('.sketch-undo').addEventListener('click', function () {
      if (!history.length) return;
      strokes = parseStrokes(history.pop());
      redraw();
      commit();
    });

    widget.querySelector('.sketch-clear').addEventListener('click', function () {
      if (!strokes.length || !confirm('Clear your whole drawing?')) return;
      remember();
      strokes = [];
      redraw();
      commit();
    });

    // The canvas takes the diagram's proportions so nothing is squashed. Until the image
    // arrives (or when there is none) a grid is shown.
    function ready() { redraw(); commit(true); }
    var src = widget.dataset.bg;
    if (src) {
      var img = new Image();
      img.onload = function () {
        bg = img;
        var h = Math.round(WIDTH * img.naturalHeight / Math.max(1, img.naturalWidth));
        canvas.height = Math.max(250, Math.min(1100, h));
        ready();
      };
      img.onerror = ready;
      img.src = src;
    } else {
      ready();
    }
  }

  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.sketch-widget').forEach(init);
  });
})();
