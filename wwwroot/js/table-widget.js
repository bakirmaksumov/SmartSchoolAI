// Wires up the editable grid for TableCheck/TableText questions (see
// TakeAssignment.cshtml's ".tbl-widget" blocks). Every keystroke in any answer
// cell re-serialises the whole grid ("r1c1,r1c2;r2c1,r2c2" — "given" columns'
// values included, so the AI grader sees the same context the student did)
// into the question's own <math-field> so take-assignment.js's existing
// answer-capture/grading pipeline picks it up unchanged.
(function () {
  function serialise(widget, rows, cols) {
    var grid = [];
    for (var r = 0; r < rows; r++) {
      var row = [];
      for (var c = 0; c < cols; c++) {
        var input = widget.querySelector('.tbl-cell[data-row="' + r + '"][data-col="' + c + '"]');
        if (input) {
          row.push((input.value || '').trim());
          continue;
        }
        var given = widget.querySelector('.given-cell[data-row="' + r + '"][data-col="' + c + '"]');
        row.push(given ? given.textContent.trim() : '');
      }
      grid.push(row.join(','));
    }
    return grid.join(';');
  }

  // take-assignment.js restores a saved draft's text into the math-field
  // before this widget inits, but never touches the individual .tbl-cell
  // inputs itself — without this, every cell renders empty after a reload
  // even though the answer was actually saved.
  function restore(widget, text, rows, cols) {
    if (!text) return;
    var gridRows = text.split(';');
    for (var r = 0; r < rows && r < gridRows.length; r++) {
      var vals = gridRows[r].split(',');
      for (var c = 0; c < cols && c < vals.length; c++) {
        var input = widget.querySelector('.tbl-cell[data-row="' + r + '"][data-col="' + c + '"]');
        if (input) input.value = vals[c];
      }
    }
  }

  function init(widget) {
    var qKey = widget.dataset.qkey;
    var mathField = document.querySelector('.answer-textarea[data-qkey="' + qKey + '"]');
    var inputs = widget.querySelectorAll('.tbl-cell');
    if (!mathField || !inputs.length) return;

    var cols = parseInt(widget.dataset.cols, 10) || 0;
    var rows = 0;
    widget.querySelectorAll('.tbl-cell, .given-cell').forEach(function (c) {
      rows = Math.max(rows, parseInt(c.dataset.row, 10) + 1);
    });
    if (!cols) {
      inputs.forEach(function (c) { cols = Math.max(cols, parseInt(c.dataset.col, 10) + 1); });
    }

    restore(widget, mathField.value, rows, cols);

    inputs.forEach(function (input) {
      input.addEventListener('input', function () {
        mathField.value = serialise(widget, rows, cols);
        mathField.dispatchEvent(new Event('input', { bubbles: true }));
      });
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.tbl-widget').forEach(init);
  });
})();
