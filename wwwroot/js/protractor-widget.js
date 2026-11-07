// Renders a clickable protractor for Protractor-type questions (see
// TakeAssignment.cshtml's ".prot-widget" blocks). The student clicks inside
// the semicircle to set an angle (0-180, snapped to the teacher's chosen
// precision); the angle is written as plain text into the question's own
// <math-field> so take-assignment.js's existing answer-capture/grading
// pipeline picks it up unchanged. Starts blank — never shows the teacher's
// target angle.
(function () {
  function snap(deg, precision) {
    var p = Math.max(1, precision || 5);
    return Math.min(180, Math.max(0, Math.round(deg / p) * p));
  }

  function geom(W) {
    var R = W * 0.43;
    var H = Math.round(R + 68);
    return { W: W, H: H, R: R, cx: W / 2, cy: H - 50 };
  }

  function render(svg, g, angle) {
    svg.setAttribute('viewBox', '0 0 ' + g.W + ' ' + g.H);
    var parts = [];

    if (angle !== null) {
      var sweepEnd = -angle * Math.PI / 180;
      var sx = g.cx + Math.cos(sweepEnd) * g.R * 1.1;
      var sy = g.cy + Math.sin(sweepEnd) * g.R * 1.1;
      var largeArc = angle > 180 ? 1 : 0;
      parts.push('<path d="M' + g.cx + ' ' + g.cy + ' L' + (g.cx + g.R * 1.1) + ' ' + g.cy +
        ' A' + (g.R * 1.1) + ' ' + (g.R * 1.1) + ' 0 ' + largeArc + ' 0 ' + sx + ' ' + sy + ' Z" fill="rgba(173,210,245,0.4)" />');
    }

    // body
    parts.push('<path d="M' + (g.cx - g.R) + ' ' + g.cy + ' A' + g.R + ' ' + g.R + ' 0 0 1 ' + (g.cx + g.R) + ' ' + g.cy +
      ' L' + (g.cx - g.R) + ' ' + g.cy + ' Z" fill="#f8f9ff" stroke="#1a1a2e" stroke-width="2" />');

    // inner cutout
    var innerR = g.R * 0.12;
    parts.push('<path d="M' + (g.cx - innerR) + ' ' + g.cy + ' A' + innerR + ' ' + innerR + ' 0 0 1 ' + (g.cx + innerR) + ' ' + g.cy +
      '" stroke="#333" stroke-width="1.5" fill="none" />');

    // ticks + labels every 10deg, minor ticks every 5deg
    var fontSize = Math.max(9, Math.round(g.R * 0.072));
    for (var d = 0; d <= 180; d += 5) {
      var canvasAngle = -d * Math.PI / 180;
      var cosA = Math.cos(canvasAngle), sinA = Math.sin(canvasAngle);
      var tickIn = d % 10 === 0 ? g.R * 0.86 : g.R * 0.90;
      var w = d % 10 === 0 ? 1.5 : 1.0;
      parts.push('<line x1="' + (g.cx + cosA * tickIn) + '" y1="' + (g.cy + sinA * tickIn) +
        '" x2="' + (g.cx + cosA * g.R) + '" y2="' + (g.cy + sinA * g.R) + '" stroke="#222" stroke-width="' + w + '" />');
      if (d % 10 === 0) {
        var labelR = g.R * 0.78;
        var lx = g.cx + cosA * labelR, ly = g.cy + sinA * labelR;
        parts.push('<text x="' + lx + '" y="' + ly + '" text-anchor="middle" dominant-baseline="middle" font-size="' + fontSize + '" fill="#111">' + d + '</text>');
      }
    }

    // baseline + centre mark
    parts.push('<line x1="' + (g.cx - g.R) + '" y1="' + g.cy + '" x2="' + (g.cx + g.R) + '" y2="' + g.cy + '" stroke="#1a1a2e" stroke-width="1.5" />');

    if (angle !== null) {
      var ang = -angle * Math.PI / 180;
      var ax = g.cx + Math.cos(ang) * g.R;
      var ay = g.cy + Math.sin(ang) * g.R;
      parts.push('<line x1="' + g.cx + '" y1="' + g.cy + '" x2="' + ax + '" y2="' + ay + '" stroke="#ff6b6b" stroke-width="2.5" />');
      parts.push('<circle cx="' + ax + '" cy="' + ay + '" r="5" fill="#ff6b6b" />');
    }
    parts.push('<circle cx="' + g.cx + '" cy="' + g.cy + '" r="4" fill="#1a1a2e" />');

    svg.innerHTML = parts.join('');
  }

  function init(widget) {
    var svg = widget.querySelector('.prot-widget-svg');
    var precision = parseFloat(widget.dataset.precision) || 5;
    var qKey = widget.dataset.qkey;
    var mathField = document.querySelector('.answer-textarea[data-qkey="' + qKey + '"]');
    if (!svg || !mathField) return;

    var W = 480;
    var g = geom(W);
    // take-assignment.js restores a saved draft's text into the math-field
    // before this widget inits — read it back so a reload doesn't silently
    // reset the angle to blank.
    var restored = parseFloat(mathField.value);
    var angle = isNaN(restored) ? null : restored;
    render(svg, g, angle);

    svg.addEventListener('click', function (e) {
      var rect = svg.getBoundingClientRect();
      var scale = W / rect.width;
      var px = (e.clientX - rect.left) * scale;
      var py = (e.clientY - rect.top) * scale;
      if (py > g.cy + 4) return; // clicked below the baseline — not on the protractor

      var deg = Math.atan2(g.cy - py, px - g.cx) * 180 / Math.PI;
      deg = Math.max(0, Math.min(180, deg));
      angle = snap(deg, precision);

      mathField.value = String(angle);
      mathField.dispatchEvent(new Event('input', { bubbles: true }));
      render(svg, g, angle);
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.prot-widget').forEach(init);
  });
})();
