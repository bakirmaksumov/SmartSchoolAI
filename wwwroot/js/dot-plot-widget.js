// Renders a clickable dot plot for DotPlot-type questions (see
// TakeAssignment.cshtml's ".dp-widget" blocks). The student stacks marks
// above each tick; the resulting distribution is written as plain text into
// the question's own <math-field> so take-assignment.js's existing
// answer-capture/grading pipeline picks it up unchanged.
(function () {
  var PAD_L = 30, PAD_T = 10, PAD_R = 20, CELL = 36;

  function fmt(v) {
    var r = Math.round(v * 100) / 100;
    return Number.isInteger(r) ? String(r) : String(r);
  }

  function drawItem(parts, cx, cy, type, ghost) {
    var alpha = ghost ? 0.18 : 1;
    var col = '#8a8fc8';
    if (type === 'dot') {
      parts.push('<circle cx="' + cx + '" cy="' + cy + '" r="7" fill="' + col + '" opacity="' + alpha + '" />');
      if (!ghost) parts.push('<circle cx="' + cx + '" cy="' + cy + '" r="3" fill="#fff" />');
    } else {
      var s = 7;
      parts.push('<path d="M' + (cx - s) + ' ' + (cy - s) + ' L' + (cx + s) + ' ' + (cy + s) +
        ' M' + (cx + s) + ' ' + (cy - s) + ' L' + (cx - s) + ' ' + (cy + s) +
        '" stroke="' + col + '" stroke-width="2" stroke-linecap="round" opacity="' + alpha + '" />');
    }
  }

  function render(widget, svg, ticks, colSize, labelStep, label, itemType, grid) {
    var cols = ticks.length;
    var W = PAD_L + cols * CELL + PAD_R;
    var H = PAD_T + colSize * CELL + (label ? 60 : 40);
    var axisY = PAD_T + colSize * CELL;
    svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H);
    widget.dataset.svgW = W; widget.dataset.svgH = H;

    var parts = [];

    for (var col = 0; col < cols; col++) {
      var cx = PAD_L + col * CELL + CELL / 2;
      for (var row = 0; row < colSize; row++) {
        var cy = axisY - (row + 1) * CELL + CELL / 2;
        drawItem(parts, cx, cy, itemType, true);
      }
    }
    for (var col2 = 0; col2 < cols; col2++) {
      var cx2 = PAD_L + col2 * CELL + CELL / 2;
      for (var row2 = 0; row2 < grid[col2]; row2++) {
        var cy2 = axisY - (row2 + 1) * CELL + CELL / 2;
        drawItem(parts, cx2, cy2, itemType, false);
      }
    }

    var axL = PAD_L - 8, axR = PAD_L + cols * CELL + 8;
    parts.push('<line x1="' + axL + '" y1="' + axisY + '" x2="' + axR + '" y2="' + axisY + '" stroke="#5c6079" stroke-width="1.8" />');

    for (var i = 0; i < cols; i++) {
      var px = PAD_L + i * CELL + CELL / 2;
      parts.push('<line x1="' + px + '" y1="' + (axisY - 5) + '" x2="' + px + '" y2="' + (axisY + 5) + '" stroke="#5c6079" stroke-width="1.2" />');
      if (i % labelStep === 0)
        parts.push('<text x="' + px + '" y="' + (axisY + 20) + '" text-anchor="middle" font-size="11" fill="#5c6079">' + fmt(ticks[i]) + '</text>');
    }
    if (label)
      parts.push('<text x="' + (W / 2) + '" y="' + (H - 4) + '" text-anchor="middle" font-size="12" fill="#5c6079">' + label + '</text>');

    svg.innerHTML = parts.join('');
  }

  function serialise(ticks, grid) {
    var values = [];
    for (var c = 0; c < grid.length; c++)
      for (var n = 0; n < grid[c]; n++) values.push(fmt(ticks[c]));
    return values.join(',');
  }

  // Reverses serialise(): counts how many times each tick's value appears in
  // the saved answer text. take-assignment.js restores that text into the
  // math-field before this widget inits — without this, a reload would start
  // the stack heights back at zero and silently discard the saved answer.
  function parseAnswer(text, ticks, colSize) {
    var grid = ticks.map(function () { return 0; });
    (text || '').split(',').forEach(function (v) {
      v = v.trim();
      if (v === '') return;
      var num = parseFloat(v);
      if (isNaN(num)) return;
      var idx = -1, bestDiff = Infinity;
      ticks.forEach(function (t, i) {
        var d = Math.abs(t - num);
        if (d < bestDiff) { bestDiff = d; idx = i; }
      });
      if (idx !== -1 && bestDiff < 1e-6 && grid[idx] < colSize) grid[idx]++;
    });
    return grid;
  }

  function init(widget) {
    var svg = widget.querySelector('.dp-widget-svg');
    var ticks = (widget.dataset.ticks || '').split(',').map(Number).filter(function (n) { return !isNaN(n); });
    var colSize = parseInt(widget.dataset.colsize, 10) || 5;
    var labelStep = parseInt(widget.dataset.labelstep, 10) || 1;
    var label = widget.dataset.label || '';
    var itemType = widget.dataset.itemtype || 'cross';
    var qKey = widget.dataset.qkey;
    var mathField = document.querySelector('.answer-textarea[data-qkey="' + qKey + '"]');
    if (!svg || !mathField || !ticks.length) return;

    var grid = parseAnswer(mathField.value, ticks, colSize);

    function redraw() { render(widget, svg, ticks, colSize, labelStep, label, itemType, grid); }
    redraw();

    svg.addEventListener('click', function (e) {
      var rect = svg.getBoundingClientRect();
      var W = parseFloat(widget.dataset.svgW), H = parseFloat(widget.dataset.svgH);
      var cx = (e.clientX - rect.left) * (W / rect.width);
      var cy = (e.clientY - rect.top) * (H / rect.height);
      var axisY = PAD_T + colSize * CELL;

      var col = Math.floor((cx - PAD_L) / CELL);
      if (col < 0 || col >= ticks.length) return;

      var rowFromTop = Math.floor((cy - PAD_T) / CELL);
      if (rowFromTop < 0 || rowFromTop >= colSize) return;
      var rowFromBase = colSize - 1 - rowFromTop;

      var filled = grid[col];
      grid[col] = rowFromBase < filled ? rowFromBase + 1 : Math.min(colSize, rowFromBase + 1);

      mathField.value = serialise(ticks, grid);
      mathField.dispatchEvent(new Event('input', { bubbles: true }));
      redraw();
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.dp-widget').forEach(init);
  });
})();
