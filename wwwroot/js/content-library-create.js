// ════════════════════════════════════════════
//  EDITOR SETUP
// ════════════════════════════════════════════
const editor = document.getElementById('questionEditor');

// Mirror content to hidden input on form submit
document.getElementById('questionForm').addEventListener('submit', () => {
  document.getElementById('titleHidden').value = extractEditorTitle();
});

// Use plain text for simple questions; keep HTML only when the title contains
// rich content (equations, tables, images). This prevents pasted inline styling
// (font/color/span) from leaking into the saved question title.
function extractEditorTitle() {
  const hasRich = editor.querySelector('.eq-chip, table, img');
  return hasRich ? editor.innerHTML.trim() : editor.textContent.trim();
}

// Paste as plain text — strips source formatting (font/color/span) so the
// question title stays clean. Toolbar features (equations, tables, images)
// still insert their own markup via execCommand and are unaffected.
editor.addEventListener('paste', e => {
  e.preventDefault();
  const text = (e.clipboardData || window.clipboardData).getData('text/plain');
  document.execCommand('insertText', false, text);
});

// Keyboard shortcuts + chip deletion
editor.addEventListener('keydown', e => {
  if ((e.ctrlKey || e.metaKey) && e.key === 'b') { e.preventDefault(); execCmd('bold'); return; }
  if ((e.ctrlKey || e.metaKey) && e.key === 'z') { e.preventDefault(); execCmd('undo'); return; }
  if ((e.ctrlKey || e.metaKey) && e.key === 'y') { e.preventDefault(); execCmd('redo'); return; }

  // Delete equation chip with Backspace or Delete key
  if (e.key === 'Backspace' || e.key === 'Delete') {
    const sel = window.getSelection();
    if (!sel.rangeCount) return;
    const range = sel.getRangeAt(0);
    if (!range.collapsed) return; // let browser handle selection delete

    let chip = null;
    if (e.key === 'Backspace') {
      // Check the node immediately before the cursor
      const node = range.startContainer;
      const offset = range.startOffset;
      if (node.nodeType === Node.TEXT_NODE && offset === 0) {
        const prev = node.previousSibling;
        if (prev && prev.classList && prev.classList.contains('eq-chip')) chip = prev;
      } else if (node.nodeType === Node.ELEMENT_NODE && offset > 0) {
        const prev = node.childNodes[offset - 1];
        if (prev && prev.classList && prev.classList.contains('eq-chip')) chip = prev;
      }
    } else {
      // Delete key — check node immediately after the cursor
      const node = range.startContainer;
      const offset = range.startOffset;
      if (node.nodeType === Node.TEXT_NODE && offset === node.length) {
        const next = node.nextSibling;
        if (next && next.classList && next.classList.contains('eq-chip')) chip = next;
      } else if (node.nodeType === Node.ELEMENT_NODE) {
        const next = node.childNodes[offset];
        if (next && next.classList && next.classList.contains('eq-chip')) chip = next;
      }
    }

    if (chip) {
      e.preventDefault();
      chip.remove();
    }
  }
});

function execCmd(cmd) {
  editor.focus();
  document.execCommand(cmd, false, null);
}

// ════════════════════════════════════════════
//  TABLE PICKER
// ════════════════════════════════════════════
const ROWS = 8, COLS = 8;
const picker = document.getElementById('tblPicker');
const label  = document.getElementById('tblPickerLabel');
let cells = [];
for (let r = 0; r < ROWS; r++) {
  for (let c = 0; c < COLS; c++) {
    const el = document.createElement('div');
    el.className = 'tbl-picker-cell';
    el.dataset.r = r; el.dataset.c = c;
    el.addEventListener('mouseenter', () => highlightGrid(r, c));
    el.addEventListener('click', () => { insertTable(r+1, c+1); closeTblDrop(); });
    picker.appendChild(el);
    cells.push(el);
  }
}

function highlightGrid(maxR, maxC) {
  cells.forEach(el => {
    el.classList.toggle('hi', +el.dataset.r <= maxR && +el.dataset.c <= maxC);
  });
  label.textContent = `${maxR+1} × ${maxC+1}`;
}

function closeTblDrop() {
  const dropdown = bootstrap.Dropdown.getInstance(document.getElementById('tblDropBtn'));
  if (dropdown) dropdown.hide();
}

function insertTable(rows, cols) {
  editor.focus();
  let html = '<table><tbody>';
  for (let r = 0; r < rows; r++) {
    html += '<tr>';
    for (let c = 0; c < cols; c++) html += '<td>&nbsp;</td>';
    html += '</tr>';
  }
  html += '</tbody></table><p><br></p>';
  document.execCommand('insertHTML', false, html);
}

function deleteTable() {
  closeTblDrop();
  editor.focus();
  const sel = window.getSelection();
  if (sel.rangeCount) {
    let node = sel.getRangeAt(0).commonAncestorContainer;
    while (node && node !== editor) {
      if (node.nodeName === 'TABLE') { node.remove(); return; }
      node = node.parentNode;
    }
  }
}

// ════════════════════════════════════════════
//  IMAGE INSERT
// ════════════════════════════════════════════
function insertImage(input) {
  const file = input.files[0];
  if (!file) return;
  const reader = new FileReader();
  reader.onload = e => {
    editor.focus();
    document.execCommand('insertHTML', false,
      `<img src="${e.target.result}" style="max-width:100%;border-radius:6px;" alt="diagram" />`);
  };
  reader.readAsDataURL(file);
  input.value = '';
}

// ════════════════════════════════════════════
//  DIAGRAM MODAL
// ════════════════════════════════════════════
let _activeDgType = 'scatter';
let _dgChart      = null;

function openDiagramModal() {
  _activeDgType = 'scatter';
  // Reset type buttons
  document.querySelectorAll('.dg-type-btn').forEach(b => {
    b.classList.toggle('btn-outline-primary', b.dataset.type === 'scatter');
    b.classList.toggle('btn-outline-secondary', b.dataset.type !== 'scatter');
    b.classList.toggle('active', b.dataset.type === 'scatter');
  });
  // Show scatter panel only
  document.querySelectorAll('.dg-panel').forEach(p => p.classList.add('d-none'));
  document.getElementById('dgPanel-scatter').classList.remove('d-none');
  new bootstrap.Modal(document.getElementById('diagramModal')).show();
}

// Diagram type tab switching
document.querySelectorAll('.dg-type-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    _activeDgType = btn.dataset.type;
    document.querySelectorAll('.dg-type-btn').forEach(b => {
      b.classList.toggle('btn-outline-primary',   b === btn);
      b.classList.toggle('btn-outline-secondary', b !== btn);
      b.classList.toggle('active', b === btn);
    });
    document.querySelectorAll('.dg-panel').forEach(p => p.classList.add('d-none'));
    document.getElementById('dgPanel-' + _activeDgType).classList.remove('d-none');
  });
});

// Distribution sub-tab switching (bar / dot / pdf)
document.querySelectorAll('[id$="DistRow"] .dg-dist-btn, #dgPanel-bar .dg-dist-btn, #dgPanel-dot .dg-dist-btn, #dgPanel-pdf .dg-dist-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    const row = btn.closest('.d-flex');
    row.querySelectorAll('.dg-dist-btn').forEach(b => {
      b.classList.toggle('btn-outline-primary',   b === btn);
      b.classList.toggle('btn-outline-secondary', b !== btn);
      b.classList.toggle('active', b === btn);
    });
    // show/hide distribution-specific fields for bar
    if (_activeDgType === 'bar') {
      const dist = btn.dataset.dist;
      document.getElementById('bar-samples').closest('.row').classList.toggle('d-none', dist === 'list');
      document.getElementById('bar-normal-fields').classList.toggle('d-none',  dist !== 'normal');
      document.getElementById('bar-skewed-fields').classList.toggle('d-none',  dist !== 'skewed');
      document.getElementById('bar-bimodal-fields').classList.toggle('d-none', dist !== 'bimodal');
      document.getElementById('bar-exp-fields').classList.toggle('d-none',     dist !== 'exponential');
      document.getElementById('bar-list-fields').classList.toggle('d-none',    dist !== 'list');
    }
    // show/hide distribution-specific fields for dot
    if (_activeDgType === 'dot') {
      const dist = btn.dataset.dist;
      document.getElementById('dot-samples-row').classList.toggle('d-none',    dist === 'list');
      document.getElementById('dot-normal-fields').classList.toggle('d-none',  dist !== 'normal');
      document.getElementById('dot-skewed-fields').classList.toggle('d-none',  dist !== 'skewed');
      document.getElementById('dot-bimodal-fields').classList.toggle('d-none', dist !== 'bimodal');
      document.getElementById('dot-exp-fields').classList.toggle('d-none',     dist !== 'exponential');
      document.getElementById('dot-list-fields').classList.toggle('d-none',    dist !== 'list');
    }
    // show/hide distribution-specific fields for pdf
    if (_activeDgType === 'pdf') {
      const dist = btn.dataset.dist;
      document.getElementById('pdf-normal-fields').classList.toggle('d-none',  dist !== 'normal');
      document.getElementById('pdf-skewed-fields').classList.toggle('d-none',  dist !== 'skewed');
      document.getElementById('pdf-bimodal-fields').classList.toggle('d-none', dist !== 'bimodal');
      document.getElementById('pdf-exp-fields').classList.toggle('d-none',     dist !== 'exponential');
      document.getElementById('pdf-list-fields').classList.toggle('d-none',    dist !== 'list');
    }
  });
});

// ── Stats helpers ──────────────────────────
function rng() { return Math.random(); }
function randNorm(mean, std) {
  // Box-Muller
  const u = 1 - rng(), v = rng();
  return mean + std * Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v);
}
function clamp(v, mn, mx) { return Math.max(mn, Math.min(mx, v)); }

function genSamples(dist, n, mean, std) {
  mean = mean || 5; std = std || 1.5;
  switch (dist) {
    case 'normal':      return Array.from({length:n}, () => randNorm(mean, std));
    case 'skewed':      return Array.from({length:n}, () => Math.abs(randNorm(0, std)) + mean - std);
    case 'bimodal':     return Array.from({length:n}, (_, i) =>
                          i < n/2 ? randNorm(mean - 2, std * 0.7) : randNorm(mean + 2, std * 0.7));
    case 'uniform':     return Array.from({length:n}, () => mean - std + rng() * std * 2);
    case 'exponential': return Array.from({length:n}, () => mean - std * Math.log(1 - rng()));
    default:            return Array.from({length:n}, () => randNorm(mean, std));
  }
}

function toBins(data, binSize, binStart, axMin, axMax) {
  if (!binSize || binSize <= 0) {
    // Sturges rule
    const range = Math.max(...data) - Math.min(...data);
    binSize = range / Math.ceil(Math.log2(data.length) + 1);
  }
  const start = binStart !== '' && !isNaN(binStart) ? +binStart : Math.floor(Math.min(...data) / binSize) * binSize;
  const end   = axMax !== '' && !isNaN(axMax) ? +axMax : Math.ceil(Math.max(...data) / binSize) * binSize;
  const bins  = {};
  for (let b = start; b < end; b += binSize) bins[b.toFixed(4)] = 0;
  data.forEach(v => {
    const key = (Math.floor((v - start) / binSize) * binSize + start).toFixed(4);
    if (bins[key] !== undefined) bins[key]++;
  });
  return { labels: Object.keys(bins).map(k => +k), counts: Object.values(bins) };
}

function normalPdf(x, mean, std) {
  return (1 / (std * Math.sqrt(2 * Math.PI))) * Math.exp(-0.5 * ((x - mean) / std) ** 2);
}

// ── Chart factory ──────────────────────────
function generateDiagram() {
  const canvas = document.getElementById('dgCanvas');
  canvas.width  = 600;
  canvas.height = 350;
  canvas.style.display = 'block';

  if (_dgChart) { _dgChart.destroy(); _dgChart = null; }

  const isDark = document.documentElement.classList.contains('dark-style');
  const gridColor  = isDark ? 'rgba(255,255,255,0.1)' : 'rgba(0,0,0,0.08)';
  const textColor  = isDark ? '#cfd3ec' : '#566a7f';
  const axisColor  = isDark ? '#434968' : '#d9dee3';
  const BLUE       = 'rgba(105,108,255,0.85)';
  const BLUE_LIGHT = 'rgba(105,108,255,0.15)';

  const baseScales = (xLabel, yLabel, axMin, axMax, ayMin, ayMax) => ({
    x: {
      title: { display: !!xLabel, text: xLabel, color: textColor },
      grid:  { color: gridColor },
      ticks: { color: textColor },
      min: axMin !== '' && !isNaN(axMin) ? +axMin : undefined,
      max: axMax !== '' && !isNaN(axMax) ? +axMax : undefined,
    },
    y: {
      title: { display: !!yLabel, text: yLabel, color: textColor },
      grid:  { color: gridColor },
      ticks: { color: textColor },
      min: ayMin !== '' && !isNaN(ayMin) ? +ayMin : undefined,
      max: ayMax !== '' && !isNaN(ayMax) ? +ayMax : undefined,
    }
  });

  let cfg = null;

  // ── SCATTER ──────────────────────────────
  if (_activeDgType === 'scatter') {
    const n        = +document.getElementById('sc-samples').value || 60;
    const nOut     = +document.getElementById('sc-outliers').value || 0;
    const slope    = +document.getElementById('sc-slope').value || 1;
    const intercept= +document.getElementById('sc-intercept').value || 0;
    const r        = clamp(+document.getElementById('sc-r').value || 0.8, -1, 1);
    const showFit  = document.getElementById('sc-bestfit').checked;
    const labelOut = document.getElementById('sc-labelout').checked;
    const axMin    = document.getElementById('sc-axmin').value;
    const axMax    = document.getElementById('sc-axmax').value || 10;
    const ayMin    = document.getElementById('sc-aymin').value;
    const ayMax    = document.getElementById('sc-aymax').value;
    const xLabel   = document.getElementById('sc-xlabel').value;
    const yLabel   = document.getElementById('sc-ylabel').value;

    const xMin = axMin !== '' ? +axMin : 0;
    const xMax = axMax !== '' ? +axMax : 10;
    const points = Array.from({length: n}, () => {
      const x = xMin + rng() * (xMax - xMin);
      const noise = randNorm(0, Math.sqrt(1 - r*r)) * (xMax - xMin) * 0.15;
      const y = slope * x + intercept + r * (x - (xMin+xMax)/2) * 0.8 + noise;
      return {x: +x.toFixed(2), y: +y.toFixed(2)};
    });
    const outliers = Array.from({length: nOut}, () => ({
      x: +(xMin + rng() * (xMax - xMin)).toFixed(2),
      y: +(Math.min(...points.map(p=>p.y)) + rng() * (Math.max(...points.map(p=>p.y)) - Math.min(...points.map(p=>p.y))) * 1.8 - Math.max(...points.map(p=>p.y)) * 0.2).toFixed(2),
      outlier: true
    }));

    const datasets = [{
      label: 'Data',
      data: points,
      backgroundColor: BLUE,
      pointRadius: 4,
      type: 'scatter'
    }];

    if (outliers.length) {
      datasets.push({
        label: 'Outliers',
        data: outliers,
        backgroundColor: 'rgba(255,62,29,0.85)',
        pointRadius: 5,
        type: 'scatter'
      });
    }

    if (showFit) {
      datasets.push({
        label: `y = ${slope}x + ${intercept}`,
        data: [{x: xMin, y: slope*xMin+intercept}, {x: xMax, y: slope*xMax+intercept}],
        type: 'line',
        borderColor: 'rgba(255,171,0,0.9)',
        borderWidth: 2,
        pointRadius: 0,
        fill: false,
      });
    }

    cfg = { type: 'scatter', data: { datasets },
      options: { responsive: false, animation: false,
        plugins: { legend: { labels: { color: textColor } } },
        scales: baseScales(xLabel, yLabel, axMin, axMax, ayMin, ayMax) } };
  }

  // ── BAR / DOT shared data gen ─────────────
  const getBarDist = () => {
    const active = document.querySelector('#dgPanel-bar .dg-dist-btn.active');
    return active ? active.dataset.dist : 'normal';
  };
  const getDotDist = () => {
    const active = document.querySelector('#dgPanel-dot .dg-dist-btn.active');
    return active ? active.dataset.dist : 'normal';
  };

  // ── BAR ───────────────────────────────────
  if (_activeDgType === 'bar') {
    const dist    = getBarDist();
    const n       = +document.getElementById('bar-samples').value || 60;
    // pick mean/std from the active distribution's fields
    const mean    = dist === 'skewed'  ? (+document.getElementById('bar-skew-mean').value || 5)
                  : dist === 'normal'  ? (+document.getElementById('bar-mean').value || 5)
                  : 5;
    const std     = dist === 'skewed'  ? (+document.getElementById('bar-skew-std').value || 1.5)
                  : dist === 'normal'  ? (+document.getElementById('bar-std').value || 1.5)
                  : 1.5;
    const lambda  = +document.getElementById('bar-lambda').value || 1;
    const binSize = +document.getElementById('bar-binsize').value;
    const binStart= document.getElementById('bar-binstart').value;
    const axMin   = document.getElementById('bar-axmin').value;
    const axMax   = document.getElementById('bar-axmax').value || 10;
    const ayMin   = document.getElementById('bar-aymin').value;
    const ayMax   = document.getElementById('bar-aymax').value;
    const xLabel  = document.getElementById('bar-xlabel').value;
    const yLabel  = document.getElementById('bar-ylabel').value;

    let data;
    if (dist === 'list') {
      const raw = document.getElementById('bar-list').value;
      data = raw.split(',').map(v => +v.trim()).filter(v => !isNaN(v));
      if (!data.length) data = genSamples('normal', n, 5, 1.5);
    } else if (dist === 'bimodal') {
      const m1 = +document.getElementById('bar-bim-mean1').value || 3;
      const s1 = +document.getElementById('bar-bim-std1').value  || 0.8;
      const w1 = +document.getElementById('bar-bim-w1').value    || 0.5;
      const m2 = +document.getElementById('bar-bim-mean2').value || 7;
      const s2 = +document.getElementById('bar-bim-std2').value  || 0.8;
      const n1 = Math.round(n * w1); const n2 = n - n1;
      data = [...Array.from({length:n1}, () => randNorm(m1, s1)),
              ...Array.from({length:n2}, () => randNorm(m2, s2))];
    } else if (dist === 'exponential') {
      data = Array.from({length:n}, () => -Math.log(1 - rng()) / lambda);
    } else if (dist === 'skewed') {
      const skew = +document.getElementById('bar-skew-skewness').value || 1;
      data = Array.from({length:n}, () => {
        const v = randNorm(mean, std);
        return mean + Math.sign(skew) * Math.abs(v - mean) * (1 + Math.abs(skew) * 0.3);
      });
    } else {
      data = genSamples(dist, n, mean, std);
    }

    const { labels, counts } = toBins(data, binSize, binStart, axMin, axMax);
    cfg = {
      type: 'bar',
      data: { labels, datasets: [{ label: 'Frequency', data: counts,
        backgroundColor: BLUE_LIGHT, borderColor: BLUE, borderWidth: 1.5 }] },
      options: { responsive: false, animation: false,
        plugins: { legend: { labels: { color: textColor } } },
        scales: { ...baseScales(xLabel, yLabel, axMin, axMax, ayMin, ayMax),
          x: { ...baseScales(xLabel,'',axMin,axMax,'','').x,
            type: 'linear', offset: false,
            barPercentage: 1, categoryPercentage: 1 } } }
    };
  }

  // ── DOT ───────────────────────────────────
  if (_activeDgType === 'dot') {
    const dist   = getDotDist();
    const n      = +document.getElementById('dot-samples').value || 60;
    const mean   = dist === 'skewed' ? (+document.getElementById('dot-skew-mean').value || 5)
                 : dist === 'normal' ? (+document.getElementById('dot-mean').value || 5)
                 : 5;
    const std    = dist === 'skewed' ? (+document.getElementById('dot-skew-std').value || 1.5)
                 : dist === 'normal' ? (+document.getElementById('dot-std').value || 1.5)
                 : 1.5;
    const lambda = +document.getElementById('dot-lambda').value || 1;
    let step     = +document.getElementById('dot-step').value;
    const axMin  = document.getElementById('dot-axmin').value;
    const axMax  = document.getElementById('dot-axmax').value || 10;
    const xLabel = document.getElementById('dot-xlabel').value;
    const yLabel = document.getElementById('dot-ylabel').value;

    let raw;
    if (dist === 'list') {
      const rawStr = document.getElementById('dot-list').value;
      raw = rawStr.split(',').map(v => +v.trim()).filter(v => !isNaN(v));
      if (!raw.length) raw = genSamples('normal', n, 5, 1.5);
    } else if (dist === 'bimodal') {
      const m1 = +document.getElementById('dot-bim-mean1').value || 3;
      const s1 = +document.getElementById('dot-bim-std1').value  || 0.8;
      const w1 = +document.getElementById('dot-bim-w1').value    || 0.5;
      const m2 = +document.getElementById('dot-bim-mean2').value || 7;
      const s2 = +document.getElementById('dot-bim-std2').value  || 0.8;
      const n1 = Math.round(n * w1); const n2 = n - n1;
      raw = [...Array.from({length:n1}, () => randNorm(m1, s1)),
             ...Array.from({length:n2}, () => randNorm(m2, s2))];
    } else if (dist === 'exponential') {
      raw = Array.from({length:n}, () => -Math.log(1 - rng()) / lambda);
    } else if (dist === 'skewed') {
      const skew = +document.getElementById('dot-skew-skewness').value || 1;
      raw = Array.from({length:n}, () => {
        const v = randNorm(mean, std);
        return mean + Math.sign(skew) * Math.abs(v - mean) * (1 + Math.abs(skew) * 0.3);
      });
    } else {
      raw = genSamples(dist, n, mean, std);
    }

    if (!step || step <= 0) {
      const range = Math.max(...raw) - Math.min(...raw);
      step = +(range / 20).toFixed(2) || 0.5;
    }
    // Stack dots per bucket
    const buckets = {};
    raw.forEach(v => {
      const k = (Math.round(v / step) * step).toFixed(4);
      buckets[k] = (buckets[k] || 0) + 1;
    });
    const dotData = [];
    Object.entries(buckets).forEach(([k, count]) => {
      for (let i = 1; i <= count; i++) dotData.push({x: +k, y: i});
    });

    cfg = {
      type: 'scatter',
      data: { datasets: [{ label: 'Dot plot', data: dotData,
        backgroundColor: BLUE, pointRadius: 5, pointStyle: 'circle' }] },
      options: { responsive: false, animation: false,
        plugins: { legend: { labels: { color: textColor } } },
        scales: baseScales(xLabel, yLabel, axMin, axMax, '', '') }
    };
  }

  // ── PDF ───────────────────────────────────
  if (_activeDgType === 'pdf') {
    const activePdfDist = document.querySelector('#dgPanel-pdf .dg-dist-btn.active');
    const dist  = activePdfDist ? activePdfDist.dataset.dist : 'normal';
    const axMin = +document.getElementById('pdf-axmin').value || 0;
    const axMax = +document.getElementById('pdf-axmax').value || 10;
    const ayMin = document.getElementById('pdf-aymin').value;
    const ayMax = document.getElementById('pdf-aymax').value;
    const xLabel= document.getElementById('pdf-xlabel').value;
    const yLabel= document.getElementById('pdf-ylabel').value;
    const steps = 200;
    const xs = Array.from({length: steps+1}, (_, i) => axMin + i*(axMax-axMin)/steps);
    let ys;
    if (dist === 'bimodal') {
      const m1 = +document.getElementById('pdf-bim-mean1').value || 3;
      const s1 = +document.getElementById('pdf-bim-std1').value  || 0.8;
      const w1 = +document.getElementById('pdf-bim-w1').value    || 0.5;
      const m2 = +document.getElementById('pdf-bim-mean2').value || 7;
      const s2 = +document.getElementById('pdf-bim-std2').value  || 0.8;
      const w2 = 1 - w1;
      ys = xs.map(x => w1 * normalPdf(x, m1, s1) + w2 * normalPdf(x, m2, s2));
    } else if (dist === 'exponential') {
      const lambda = +document.getElementById('pdf-lambda').value || 1;
      ys = xs.map(x => x >= 0 ? lambda * Math.exp(-lambda * x) : 0);
    } else if (dist === 'list') {
      const rawStr = document.getElementById('pdf-list').value;
      const pts = rawStr.split(',').map(v => +v.trim()).filter(v => !isNaN(v));
      const m = pts.length ? pts.reduce((a,b)=>a+b,0)/pts.length : 5;
      const s = pts.length > 1 ? Math.sqrt(pts.reduce((a,b)=>a+(b-m)**2,0)/(pts.length-1)) : 1.5;
      ys = xs.map(x => normalPdf(x, m, s));
    } else if (dist === 'skewed') {
      const mean = +document.getElementById('pdf-skew-mean').value || 5;
      const std  = +document.getElementById('pdf-skew-std').value  || 1.5;
      const skew = +document.getElementById('pdf-skew-skewness').value || 1;
      // Approximate skewed PDF via shifted normal
      ys = xs.map(x => {
        const z = (x - mean) / std;
        return (2 / std) * normalPdf(z, 0, 1) * (skew > 0 ? (z > 0 ? 1 : 0.2) : (z < 0 ? 1 : 0.2));
      });
    } else if (dist === 'uniform') {
      ys = xs.map(x => (x >= axMin && x <= axMax) ? 1 / (axMax - axMin) : 0);
    } else {
      const mean = +document.getElementById('pdf-mean').value || 5;
      const std  = +document.getElementById('pdf-std').value  || 1.5;
      ys = xs.map(x => normalPdf(x, mean, std));
    }
    cfg = {
      type: 'line',
      data: { labels: xs.map(x => x.toFixed(2)),
        datasets: [{ label: 'PDF', data: ys, borderColor: BLUE,
          backgroundColor: BLUE_LIGHT, fill: true, pointRadius: 0, tension: 0.4 }] },
      options: { responsive: false, animation: false,
        plugins: { legend: { labels: { color: textColor } } },
        scales: baseScales(xLabel, yLabel, axMin, axMax, ayMin, ayMax) }
    };
  }

  // ── BOX ───────────────────────────────────
  if (_activeDgType === 'box') {
    const minV  = +document.getElementById('box-min').value;
    const q1    = +document.getElementById('box-q1').value;
    const med   = +document.getElementById('box-median').value;
    const avg   = document.getElementById('box-avg').value;
    const q3    = +document.getElementById('box-q3').value;
    const maxV  = +document.getElementById('box-max').value;
    const axMin = +document.getElementById('box-axmin').value;
    const axMax = +document.getElementById('box-axmax').value;
    const xLabel= document.getElementById('box-xlabel').value;

    // Draw box-plot manually on canvas (Chart.js core has no box-plot type)
    const ctx = canvas.getContext('2d');
    const W = canvas.width, H = canvas.height;
    ctx.clearRect(0, 0, W, H);

    const PAD_L = 60, PAD_R = 30, PAD_T = 30, PAD_B = 50;
    const plotW = W - PAD_L - PAD_R;
    const plotH = H - PAD_T - PAD_B;
    const cy    = PAD_T + plotH / 2;
    const boxH  = plotH * 0.4;
    const scale = v => PAD_L + ((v - axMin) / (axMax - axMin)) * plotW;

    // Grid lines + axis
    ctx.strokeStyle = isDark ? 'rgba(255,255,255,0.1)' : 'rgba(0,0,0,0.08)';
    ctx.lineWidth = 1;
    const ticks = 5;
    for (let i = 0; i <= ticks; i++) {
      const x = PAD_L + (plotW / ticks) * i;
      ctx.beginPath(); ctx.moveTo(x, PAD_T); ctx.lineTo(x, PAD_T + plotH); ctx.stroke();
      ctx.fillStyle = textColor;
      ctx.font = '11px sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText((axMin + (axMax - axMin) / ticks * i).toFixed(1), x, PAD_T + plotH + 18);
    }
    // X-axis line
    ctx.strokeStyle = axisColor;
    ctx.lineWidth = 1.5;
    ctx.beginPath(); ctx.moveTo(PAD_L, PAD_T + plotH); ctx.lineTo(W - PAD_R, PAD_T + plotH); ctx.stroke();

    // Whiskers
    ctx.strokeStyle = BLUE.replace('0.85','1');
    ctx.lineWidth = 2;
    const draw = (x1,y1,x2,y2) => { ctx.beginPath(); ctx.moveTo(x1,y1); ctx.lineTo(x2,y2); ctx.stroke(); };
    draw(scale(minV), cy, scale(q1),  cy);   // left whisker
    draw(scale(q3),   cy, scale(maxV),cy);   // right whisker
    draw(scale(minV), cy-boxH*0.3, scale(minV), cy+boxH*0.3);
    draw(scale(maxV), cy-boxH*0.3, scale(maxV), cy+boxH*0.3);

    // Box
    ctx.fillStyle = BLUE_LIGHT;
    ctx.strokeStyle = BLUE.replace('0.85','1');
    ctx.lineWidth = 2;
    ctx.fillRect(scale(q1), cy - boxH/2, scale(q3) - scale(q1), boxH);
    ctx.strokeRect(scale(q1), cy - boxH/2, scale(q3) - scale(q1), boxH);

    // Median
    ctx.strokeStyle = 'rgba(255,171,0,0.95)';
    ctx.lineWidth = 3;
    draw(scale(med), cy - boxH/2, scale(med), cy + boxH/2);

    // Average (diamond)
    if (avg !== '' && !isNaN(+avg)) {
      const ax = scale(+avg);
      ctx.fillStyle = 'rgba(255,62,29,0.9)';
      ctx.beginPath();
      ctx.moveTo(ax, cy - boxH*0.3); ctx.lineTo(ax+7, cy);
      ctx.lineTo(ax, cy + boxH*0.3); ctx.lineTo(ax-7, cy);
      ctx.closePath(); ctx.fill();
    }

    // X label
    if (xLabel) {
      ctx.fillStyle = textColor;
      ctx.font = '12px sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText(xLabel, W/2, H - 8);
    }

    // Insert as image and close
    const dataUrl = canvas.toDataURL('image/png');
    canvas.style.display = 'none';
    editor.focus();
    document.execCommand('insertHTML', false,
      `<img src="${dataUrl}" contenteditable="false"
        style="max-width:100%;border-radius:6px;display:block;margin:8px 0;"
        alt="Box plot" /><p><br></p>`);
    bootstrap.Modal.getInstance(document.getElementById('diagramModal')).hide();
    return;
  }

  if (!cfg) return;

  // Render Chart.js to canvas, then export as image
  _dgChart = new Chart(document.getElementById('dgCanvas'), cfg);
  // Wait one frame for chart to paint
  requestAnimationFrame(() => {
    const dataUrl = document.getElementById('dgCanvas').toDataURL('image/png');
    if (_dgChart) { _dgChart.destroy(); _dgChart = null; }
    canvas.style.display = 'none';
    editor.focus();
    const typeLabels = { scatter:'Scatter plot', bar:'Bar plot', dot:'Dot plot', pdf:'Probability density function' };
    document.execCommand('insertHTML', false,
      `<img src="${dataUrl}" contenteditable="false"
        style="max-width:100%;border-radius:6px;display:block;margin:8px 0;"
        alt="${typeLabels[_activeDgType]}" /><p><br></p>`);
    bootstrap.Modal.getInstance(document.getElementById('diagramModal')).hide();
  });
}

// ════════════════════════════════════════════
//  EQUATION MODAL
// ════════════════════════════════════════════
const EQ_SYMBOLS = [
  { label: 'x²',      latex: 'x^{2}' },
  { label: '√x',      latex: '\\sqrt{x}' },
  { label: 'x/y',     latex: '\\frac{x}{y}' },
  { label: 'd/dx',    latex: '\\frac{d}{dx}' },
  { label: '∑',       latex: '\\sum' },
  { label: '±',       latex: '\\pm' },
  { label: '≠',       latex: '\\neq' },
  { label: '≥',       latex: '\\geq' },
  { label: '≱',       latex: '\\ngeq' },
  { label: 'π',       latex: '\\pi' },
  { label: '∠',       latex: '\\angle' },
  { label: 'x³',      latex: 'x^{3}' },
  { label: '∛x',      latex: '\\sqrt[3]{x}' },
  { label: '|x|',     latex: '|x|' },
  { label: '∂/∂y',    latex: '\\frac{\\partial}{\\partial y}' },
  { label: 'Π',       latex: '\\prod' },
  { label: '·',       latex: '\\cdot' },
  { label: '=',       latex: '=' },
  { label: '≤',       latex: '\\leq' },
  { label: '≰',       latex: '\\nleq' },
  { label: 'θ',       latex: '\\theta' },
  { label: '"',       latex: '\\text{""}' },
  { label: 'xⁿ',      latex: 'x^{n}' },
  { label: 'ⁿ√x',     latex: '\\sqrt[n]{x}' },
  { label: '∫',       latex: '\\int' },
  { label: '∫∫',      latex: '\\iint' },
  { label: 'x̄',       latex: '\\bar{x}' },
  { label: '×',       latex: '\\times' },
  { label: '≈',       latex: '\\approx' },
  { label: 'Δ',       latex: '\\Delta' },
  { label: '≯',       latex: '\\ngtr' },
  { label: 'α',       latex: '\\alpha' },
  { label: 'z⃗',       latex: '\\vec{z}' },
  { label: 'xᵧ',      latex: 'x_{y}' },
  { label: 'logₐ',    latex: '\\log_{a}' },
  { label: '⌊',       latex: '\\lfloor' },
  { label: 'lim',     latex: '\\lim' },
  { label: 'C(n,r)',  latex: '\\binom{n}{r}' },
  { label: '÷',       latex: '\\div' },
  { label: '~',       latex: '\\sim' },
  { label: '∞',       latex: '\\infty' },
  { label: '≮',       latex: '\\nless' },
  { label: 'β',       latex: '\\beta' },
  { label: '≲',       latex: '\\lesssim' },
];
const EQ_FUNCTIONS = [
  { label: 'sin',     latex: '\\sin(' },
  { label: 'cos',     latex: '\\cos(' },
  { label: 'tan',     latex: '\\tan(' },
  { label: 'sin⁻¹',  latex: '\\sin^{-1}(' },
  { label: 'cos⁻¹',  latex: '\\cos^{-1}(' },
  { label: 'tan⁻¹',  latex: '\\tan^{-1}(' },
  { label: 'ln',      latex: '\\ln(' },
  { label: 'log',     latex: '\\log(' },
  { label: 'log₂',    latex: '\\log_{2}(' },
  { label: 'exp',     latex: '\\exp(' },
  { label: 'eˣ',      latex: 'e^{x}' },
  { label: '10ˣ',     latex: '10^{x}' },
  { label: 'n!',      latex: 'n!' },
  { label: '%',       latex: '\\%' },
  { label: 'f(x)',    latex: 'f(x)' },
  { label: 'g(x)',    latex: 'g(x)' },
];

function buildEqKeys(keys, containerId) {
  const container = document.getElementById(containerId);
  keys.forEach(k => {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn-outline-secondary ek';
    btn.textContent = k.label;
    btn.title = k.latex;
    btn.addEventListener('click', () => appendLatex(k.latex));
    container.appendChild(btn);
  });
}

buildEqKeys(EQ_SYMBOLS,   'eqKeysMain');
buildEqKeys(EQ_FUNCTIONS, 'eqKeysFn');

function appendLatex(fragment) {
  const inp = document.getElementById('eqInput');
  const pos = inp.selectionStart;
  const val = inp.value;
  inp.value = val.slice(0, pos) + fragment + val.slice(inp.selectionEnd);
  inp.selectionStart = inp.selectionEnd = pos + fragment.length;
  inp.focus();
  updateEqPreview();
}

function updateEqPreview() {
  const raw = document.getElementById('eqInput').value.trim();
  const prev = document.getElementById('eqPreview');
  if (!raw) { prev.innerHTML = '<span class="text-muted small">Preview will appear here</span>'; return; }
  try {
    prev.innerHTML = katex.renderToString(raw, { throwOnError: false, displayMode: true });
  } catch(e) {
    prev.innerHTML = `<span class="text-danger small">${e.message}</span>`;
  }
}

function openEqModal() {
  document.getElementById('eqInput').value = '';
  updateEqPreview();
  new bootstrap.Modal(document.getElementById('eqModal')).show();
}

function insertEquation() {
  const raw = document.getElementById('eqInput').value.trim();
  if (!raw) return;
  let rendered = '';
  try {
    rendered = katex.renderToString(raw, { throwOnError: false, displayMode: false });
  } catch(e) { rendered = raw; }
  // contenteditable=false makes the chip atomic — treated as one unit by the browser
  const chip = `<span class="eq-chip" contenteditable="false" data-latex="${raw.replace(/"/g,"&quot;")}"
    onclick="editEquation(this)" title="Click to edit">${rendered}</span>`;
  editor.focus();
  document.execCommand('insertHTML', false, chip);
  bootstrap.Modal.getInstance(document.getElementById('eqModal')).hide();
}

// Re-open equation for editing when user clicks on it
function editEquation(chip) {
  const latex = chip.dataset.latex;
  document.getElementById('eqInput').value = latex;
  updateEqPreview();
  const modal = new bootstrap.Modal(document.getElementById('eqModal'));
  // On save — replace chip instead of inserting new
  document.querySelector('#eqModal .btn-primary').onclick = () => {
    const newLatex = document.getElementById('eqInput').value.trim();
    if (!newLatex) return;
    let rendered = '';
    try { rendered = katex.renderToString(newLatex, { throwOnError: false, displayMode: false }); }
    catch(e) { rendered = newLatex; }
    chip.dataset.latex = newLatex;
    chip.innerHTML = rendered;
    chip.setAttribute('contenteditable', 'false');
    modal.hide();
    document.querySelector('#eqModal .btn-primary').onclick = insertEquation;
  };
  modal.show();
}

// ════════════════════════════════════════════
//  TYPE PICKER
// ════════════════════════════════════════════
const TYPE_META = {
  0:  { label: 'Text / Numeric',             icon: '<i class="bx bx-text text-primary"></i>',              panels: [] },
  4:  { label: 'Single choice',              icon: '<i class="bx bx-check-circle text-success"></i>',      panels: ['panelChoices'] },
  1:  { label: 'Multiple choice',            icon: '<i class="bx bx-list-check text-primary"></i>',        panels: ['panelChoices'] },
  5:  { label: 'Graphing',                   icon: '<i class="bx bx-line-chart text-info"></i>',           panels: ['panelGraphing'] },
  6:  { label: 'Geometry drawing',           icon: '<i class="bx bx-shape-triangle text-warning"></i>',    panels: ['panelGeometry'] },
  7:  { label: 'Number line',                icon: '<i class="bx bx-right-arrow-alt text-secondary"></i>', panels: ['panelNumberLine'] },
  8:  { label: 'Dot plot',                   icon: '<i class="bx bx-dots-horizontal-rounded text-danger"></i>', panels: ['panelDotPlot'] },
  9:  { label: 'Table check',                icon: '<i class="bx bx-grid-alt text-primary"></i>',          panels: ['panelTable'] },
  10: { label: 'Table text',                 icon: '<i class="bx bx-table text-secondary"></i>',           panels: ['panelTable'] },
  11: { label: 'Vertical operation',         icon: '<i class="bx bx-list-ol text-info"></i>',             panels: ['panelVertical'] },
  12: { label: 'Protractor',                 icon: '<i class="bx bx-compass text-danger"></i>',            panels: ['panelProtractor'] },
  13: { label: 'Rectangular fraction model', icon: '<i class="bx bx-grid text-success"></i>',              panels: ['panelFraction'] },
};

const VISUAL_MSGS = {
  8:  'Dot plot: students place dots on a number line. Describe the expected distribution in the Answer Key.',
};

document.querySelectorAll('.type-opt').forEach(btn => {
  btn.addEventListener('click', () => {
    const val = +btn.dataset.val;
    document.getElementById('questionType').value = val;
    const meta = TYPE_META[val];
    document.getElementById('typeDropLabel').textContent = meta.label;
    document.getElementById('typeDropIcon').innerHTML    = meta.icon;
    // active state
    document.querySelectorAll('.type-opt').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    // close dropdown
    bootstrap.Dropdown.getInstance(document.getElementById('typeDropBtn'))?.hide();
    // show/hide panels
    toggleTypePanels(val);
  });
});

function toggleTypePanels(val) {
  const allPanels = ['panelChoices','panelNumberLine','panelTable','panelVertical','panelFraction','panelOpenText','panelGraphing','panelGeometry','panelDotPlot','panelProtractor'];
  allPanels.forEach(id => { const el = document.getElementById(id); if (el) el.style.display = 'none'; });

  const meta = TYPE_META[val] || { panels: [] };
  meta.panels.forEach(id => {
    const el = document.getElementById(id);
    if (el) el.style.display = 'block';
  });

  // Special sub-labels
  if (val === 4) {
    document.getElementById('choicesCardTitle').innerHTML = '<i class="bx bx-check-circle me-2 text-success"></i>Answer Choices (Single)';
  } else if (val === 1) {
    document.getElementById('choicesCardTitle').innerHTML = '<i class="bx bx-list-check me-2 text-primary"></i>Answer Choices (Multiple)';
  }
  if (val === 9) document.getElementById('tableCardTitle').innerHTML = '<i class="bx bx-grid-alt me-2 text-primary"></i>Table (Check)';
  if (val === 10) document.getElementById('tableCardTitle').innerHTML = '<i class="bx bx-table me-2 text-secondary"></i>Table (Text)';
  if (val === 9 || val === 10) setTimeout(sstInit, 10);
  if (VISUAL_MSGS[val]) { const vmEl = document.getElementById('panelVisualMsg'); if (vmEl) vmEl.innerHTML = VISUAL_MSGS[val]; }

  // Vertical op sign sync
  if (val === 11) syncVertOpSign();

  // Fraction preview
  if (val === 13) renderFractionPreview();

  // Dot plot canvas
  if (val === 8) setTimeout(dpInit, 50);

  // Number line canvas
  if (val === 7) setTimeout(nlInit, 50);

  // Graphing canvas
  if (val === 5) setTimeout(gphInit, 50);

  // Geometry canvas
  if (val === 6) setTimeout(geoInit, 50);

  // Protractor canvas
  if (val === 12) setTimeout(protInit, 50);
}

// ════════════════════════════════════════════
//  CHOICE OPTIONS
// ════════════════════════════════════════════
const LETTERS = ['A','B','C','D','E','F'];

function toggleOptions() {} // kept for back-compat

function addOption() {
  const list  = document.getElementById('optionsList');
  const count = list.children.length;
  if (count >= 6) { alert('Maximum 6 options allowed.'); return; }
  const div = document.createElement('div');
  div.className = 'input-group mb-2';
  div.innerHTML =
    `<span class="input-group-text fw-bold">${LETTERS[count]}</span>
     <input type="text" class="form-control" name="options" placeholder="Option ${LETTERS[count]}" />
     <button type="button" class="btn btn-outline-danger" onclick="removeOption(this)">
       <i class="bx bx-x"></i>
     </button>`;
  list.appendChild(div);
}

function removeOption(btn) {
  const list = document.getElementById('optionsList');
  if (list.children.length <= 2) { alert('Minimum 2 options required.'); return; }
  btn.closest('.input-group').remove();
  reindexOptions();
}

function reindexOptions() {
  [...document.getElementById('optionsList').children].forEach((row, i) => {
    row.querySelector('.input-group-text').textContent = LETTERS[i];
    row.querySelector('input').placeholder = `Option ${LETTERS[i]}`;
  });
}

// ════════════════════════════════════════════
//  TABLE BUILDER  (spreadsheet-style)
// ════════════════════════════════════════════
let sstRows = 5;   // data rows (excluding header)
let sstCols = 4;   // data columns
let sstColGiven = [false, false, false, false];   // per-column: shown to students as-is vs. blank for their answer

// Called when the table panel becomes visible
function sstInit() {
  sstRows = 5; sstCols = 4;
  sstColGiven = sstColGiven.slice(0, sstCols);
  while (sstColGiven.length < sstCols) sstColGiven.push(false);
  sstRender();
}

function sstRender() {
  const wrap = document.getElementById('sstGrid');
  if (!wrap) return;

  // Build a real <table> so columns/rows always render correctly
  const tbl = document.createElement('table');
  tbl.className = 'sst-table';

  // ── HEADER ROW ──
  const thead = tbl.createTHead();
  const htr   = thead.insertRow();

  // corner cell
  const corner = document.createElement('th');
  corner.className = 'sst-corner';
  htr.appendChild(corner);

  for (let c = 0; c < sstCols; c++) {
    const th  = document.createElement('th');
    th.className = 'sst-header';
    const inp = document.createElement('input');
    inp.type        = 'text';
    inp.className   = 'sst-input sst-header-input';
    inp.placeholder = `Header ${c + 1}`;
    inp.name        = 'tableHeaders';
    th.appendChild(inp);

    // Per-column: is this data given to students as-is, or left blank as
    // their answer? Defaults to "answer" — matches how every cell behaved
    // before this toggle existed.
    const givenLabel = document.createElement('label');
    givenLabel.className = 'sst-given-toggle d-flex align-items-center gap-1 mt-1';
    givenLabel.style.cssText = 'font-weight:normal;font-size:.72rem;color:#697a8d;cursor:pointer;';
    const givenChk = document.createElement('input');
    givenChk.type = 'checkbox';
    givenChk.name = `tableColGiven_${c}`;
    givenChk.checked = !!sstColGiven[c];
    // Only updates the tracked state — never re-renders, which would wipe
    // whatever the teacher has already typed into the grid.
    givenChk.addEventListener('change', () => { sstColGiven[c] = givenChk.checked; });
    givenLabel.appendChild(givenChk);
    givenLabel.appendChild(document.createTextNode('Given to students'));
    th.appendChild(givenLabel);

    htr.appendChild(th);
  }

  // "+col" button cell (right edge of header)
  const addColTh = document.createElement('th');
  addColTh.className = 'sst-add-col-cell';
  addColTh.innerHTML = '<button type="button" class="sst-add-col-btn" onclick="sstAddCol()"><i class="bx bx-plus"></i></button>';
  htr.appendChild(addColTh);

  // ── DATA ROWS ──
  const tbody = tbl.createTBody();
  for (let r = 0; r < sstRows; r++) {
    const tr = tbody.insertRow();
    tr.className = r % 2 === 0 ? 'sst-row-even' : '';

    // row number
    const numTd = document.createElement('td');
    numTd.className = 'sst-rownum';
    numTd.textContent = r + 1;
    tr.appendChild(numTd);

    for (let c = 0; c < sstCols; c++) {
      const td  = document.createElement('td');
      td.className = 'sst-data-cell';
      const inp = document.createElement('input');
      inp.type      = 'text';
      inp.className = 'sst-input';
      inp.name      = 'tableCell';
      td.appendChild(inp);
      tr.appendChild(td);
    }

    // right spacer aligned with +col column
    const spacerTd = document.createElement('td');
    spacerTd.className = 'sst-spacer';
    tr.appendChild(spacerTd);
  }

  wrap.innerHTML = '';
  wrap.appendChild(tbl);
}

function sstAddRow() {
  sstRows++;
  sstRender();
}

function sstAddCol() {
  sstCols++;
  sstColGiven.push(false);
  sstRender();
}

// Legacy aliases kept in case anything still calls them
function addTableRow() { sstAddRow(); }
function addTableCol() { sstAddCol(); }

// ════════════════════════════════════════════
//  VERTICAL OPERATION
// ════════════════════════════════════════════
function syncVertOpSign() {
  const op = document.querySelector('[name="vertOp"]')?.value || '+';
  document.getElementById('vertOpSign').textContent = op;
}
document.querySelector('[name="vertOp"]')?.addEventListener('change', syncVertOpSign);

function addVertOperand() {
  const list = document.getElementById('vertOperandList');
  const op   = document.querySelector('[name="vertOp"]')?.value || '+';
  const div  = document.createElement('div');
  div.className = 'input-group';
  div.style.maxWidth = '220px';
  div.innerHTML = `<span class="input-group-text bg-transparent border-end-0">${op}</span>
    <input type="text" class="form-control text-end font-monospace" name="vertOperands" placeholder="number" />`;
  list.appendChild(div);
}

// ════════════════════════════════════════════
//  FRACTION MODEL PREVIEW
// ════════════════════════════════════════════
function renderFractionPreview() {
  const rows    = +document.querySelector('[name="fracRows"]')?.value  || 2;
  const cols    = +document.querySelector('[name="fracCols"]')?.value  || 4;
  const shaded  = +document.querySelector('[name="fracShaded"]')?.value || 3;
  const total   = rows * cols;
  const preview = document.getElementById('fracPreview');
  if (!preview) return;
  let html = '<div style="display:inline-grid;gap:2px;grid-template-columns:repeat(' + cols + ',32px);">';
  for (let i = 0; i < total; i++) {
    const filled = i < shaded;
    html += `<div style="width:32px;height:32px;border:1.5px solid #696cff;border-radius:3px;background:${filled ? 'rgba(105,108,255,0.35)' : 'transparent'};"></div>`;
  }
  html += '</div>';
  html += `<p class="small text-muted mt-2 mb-0">${shaded}/${total} = ${shaded}/${total}</p>`;
  preview.innerHTML = html;
}

// ════════════════════════════════════════════
//  GRAPHING ENGINE
// ════════════════════════════════════════════
let gphCtx, gphCanvas, gphW, gphH;
let gphXMin=-10,gphXMax=10,gphYMin=-10,gphYMax=10;
let gphMode='select';
let gphElements=[];
let gphHistory=[];
let gphSelected=null;
let gphPending=[];
let gphDragging=null;

const GPH_COLORS = { point:'#696cff',segment:'#696cff',linear:'#696cff',quadratic:'#696cff',ray:'#696cff',circle:'#696cff' };
const GPH_POINT_NEEDED = { point:1, segment:2, linear:2, ray:2, quadratic:3, circle:2 };

function gphInit() {
  gphCanvas = document.getElementById('gphCanvas');
  if (!gphCanvas || gphCanvas._ready) return;
  gphCanvas._ready = true;
  const panel = document.getElementById('panelGraphing');
  const sw = panel.querySelector('.flex-grow-1').clientWidth || 480;
  const size = Math.min(Math.max(sw - 20, 300), 520);
  gphCanvas.width  = size;
  gphCanvas.height = size;
  gphCanvas.style.width  = size + 'px';
  gphCanvas.style.height = size + 'px';
  gphW = gphH = size;
  gphCtx = gphCanvas.getContext('2d');

  document.querySelectorAll('.gph-mode-btn').forEach(b => {
    b.addEventListener('click', () => {
      gphMode = b.dataset.mode;
      document.querySelectorAll('.gph-mode-btn').forEach(x => x.classList.remove('active'));
      b.classList.add('active');
      gphPending = [];
      gphRedraw();
    });
  });

  gphCanvas.addEventListener('click',      gphOnClick);
  gphCanvas.addEventListener('mousedown',  gphOnMouseDown);
  gphCanvas.addEventListener('mousemove',  gphOnMouseMove);
  gphCanvas.addEventListener('mouseup',    gphOnMouseUp);
  gphCanvas.addEventListener('mouseleave', gphOnMouseUp);
  gphRedraw();
}

function gphToCanvas(gx, gy) {
  return [
    (gx - gphXMin) / (gphXMax - gphXMin) * gphW,
    (1 - (gy - gphYMin) / (gphYMax - gphYMin)) * gphH
  ];
}
function gphToGraph(cx, cy) {
  return [
    cx / gphW * (gphXMax - gphXMin) + gphXMin,
    (1 - cy / gphH) * (gphYMax - gphYMin) + gphYMin
  ];
}
function gphSnap(v) { return Math.round(v * 2) / 2; }
function gphSnapPt(cx, cy) {
  const [gx,gy] = gphToGraph(cx,cy);
  return [gphSnap(gx), gphSnap(gy)];
}
function gphGetPos(e) {
  const r  = gphCanvas.getBoundingClientRect();
  const sx = gphCanvas.width  / r.width;
  const sy = gphCanvas.height / r.height;
  return [(e.clientX - r.left)*sx, (e.clientY - r.top)*sy];
}

function gphRedraw() {
  if (!gphCtx) return;
  const c = gphCtx;
  c.clearRect(0,0,gphW,gphH);
  c.fillStyle = '#fff';
  c.fillRect(0,0,gphW,gphH);

  // grid
  c.strokeStyle = '#e0e4f0'; c.lineWidth = 1;
  for (let x=Math.ceil(gphXMin); x<=gphXMax; x++) {
    const [px] = gphToCanvas(x,0);
    c.beginPath(); c.moveTo(px,0); c.lineTo(px,gphH); c.stroke();
  }
  for (let y=Math.ceil(gphYMin); y<=gphYMax; y++) {
    const [,py] = gphToCanvas(0,y);
    c.beginPath(); c.moveTo(0,py); c.lineTo(gphW,py); c.stroke();
  }

  // axes
  c.strokeStyle = '#5c6079'; c.lineWidth = 1.8;
  const [ax0,ay0]=gphToCanvas(0,gphYMin), [ax1,ay1]=gphToCanvas(0,gphYMax);
  const [bx0,by0]=gphToCanvas(gphXMin,0), [bx1,by1]=gphToCanvas(gphXMax,0);
  c.beginPath(); c.moveTo(ax0,ay0-14); c.lineTo(ax0,ay1+14); c.stroke();
  c.beginPath(); c.moveTo(bx0-14,by0); c.lineTo(bx1+14,by0); c.stroke();
  gphArrow(c,ax0,ay0,ax1,ay1);       // top arrow
  gphArrow(c,bx0,by0,bx1,by1);       // right arrow
  gphArrow(c,ax0,ay1,ax0,ay0);       // bottom arrow
  gphArrow(c,bx1,by0,bx0,by0);       // left arrow

  // tick labels
  c.fillStyle='#5c6079'; c.font='11px system-ui';
  c.textAlign='center';
  for (let x=Math.ceil(gphXMin); x<=gphXMax; x++) {
    if(x===0) continue;
    const [px,py]=gphToCanvas(x,0); c.fillText(x,px,py+14);
  }
  c.textAlign='right';
  for (let y=Math.ceil(gphYMin); y<=gphYMax; y++) {
    if(y===0) continue;
    const [px,py]=gphToCanvas(0,y); c.fillText(y,px-4,py+4);
  }

  // elements + pending
  gphElements.forEach((el,i) => gphDrawElement(c,el,i===gphSelected));
  gphPending.forEach(pt => {
    const [px,py]=gphToCanvas(...pt);
    c.beginPath(); c.arc(px,py,5,0,Math.PI*2);
    c.fillStyle='rgba(105,108,255,0.5)'; c.fill();
  });
}

function gphArrow(c,fx,fy,tx,ty) {
  const ang=Math.atan2(ty-fy,tx-fx), len=8;
  c.beginPath();
  c.moveTo(tx,ty);
  c.lineTo(tx-len*Math.cos(ang-0.4), ty-len*Math.sin(ang-0.4));
  c.lineTo(tx-len*Math.cos(ang+0.4), ty-len*Math.sin(ang+0.4));
  c.closePath(); c.fillStyle='#5c6079'; c.fill();
}

function gphDot(c,px,py,col) {
  c.fillStyle=col; c.beginPath(); c.arc(px,py,5,0,Math.PI*2); c.fill();
  c.fillStyle='#fff'; c.beginPath(); c.arc(px,py,2.5,0,Math.PI*2); c.fill();
}

function gphDrawElement(c,el,selected) {
  const col = selected ? '#ff6b6b' : (GPH_COLORS[el.type]||'#696cff');
  c.strokeStyle=col; c.fillStyle=col; c.lineWidth=2;

  if (el.type==='point') {
    const [px,py]=gphToCanvas(...el.pts[0]);
    c.beginPath(); c.arc(px,py,6,0,Math.PI*2); c.fill();
    c.fillStyle='#fff'; c.beginPath(); c.arc(px,py,3,0,Math.PI*2); c.fill();
  } else if (el.type==='segment') {
    const [x1,y1]=gphToCanvas(...el.pts[0]), [x2,y2]=gphToCanvas(...el.pts[1]);
    c.beginPath(); c.moveTo(x1,y1); c.lineTo(x2,y2); c.stroke();
    gphDot(c,x1,y1,col); gphDot(c,x2,y2,col);
  } else if (el.type==='linear') {
    const [x1,y1]=gphToCanvas(...el.pts[0]), [x2,y2]=gphToCanvas(...el.pts[1]);
    const dx=x2-x1, dy=y2-y1, ext=3000;
    c.beginPath(); c.moveTo(x1-dx*ext,y1-dy*ext); c.lineTo(x1+dx*ext,y1+dy*ext); c.stroke();
    gphDot(c,x1,y1,col); gphDot(c,x2,y2,col);
  } else if (el.type==='ray') {
    const [x1,y1]=gphToCanvas(...el.pts[0]), [x2,y2]=gphToCanvas(...el.pts[1]);
    const dx=x2-x1, dy=y2-y1, ang=Math.atan2(dy,dx);
    c.beginPath(); c.moveTo(x1,y1); c.lineTo(x1+dx*3000,y1+dy*3000); c.stroke();
    gphDot(c,x1,y1,col);
    c.beginPath(); c.moveTo(x2,y2);
    c.lineTo(x2-10*Math.cos(ang-0.4),y2-10*Math.sin(ang-0.4));
    c.lineTo(x2-10*Math.cos(ang+0.4),y2-10*Math.sin(ang+0.4));
    c.closePath(); c.fill();
  } else if (el.type==='quadratic') {
    const [gx1,gy1]=el.pts[0],[gx2,gy2]=el.pts[1],[gx3,gy3]=el.pts[2];
    c.beginPath();
    for (let t=0,first=true; t<=1.001; t+=0.01) {
      const mt=1-t, mt2=mt*mt, t2=t*t;
      const [px,py]=gphToCanvas(mt2*gx1+2*mt*t*gx2+t2*gx3, mt2*gy1+2*mt*t*gy2+t2*gy3);
      first ? c.moveTo(px,py) : c.lineTo(px,py); first=false;
    }
    c.stroke();
    el.pts.forEach(p => gphDot(c,...gphToCanvas(...p),col));
  } else if (el.type==='circle') {
    const [cx,cy]=gphToCanvas(...el.pts[0]);
    const r = Math.abs(gphToCanvas(el.pts[0][0]+el.r, el.pts[0][1])[0] - cx);
    c.beginPath(); c.arc(cx,cy,r,0,Math.PI*2); c.stroke();
    gphDot(c,cx,cy,col);
  }
}

function gphOnClick(e) {
  if (gphDragging) return;
  const [cx,cy]=gphGetPos(e);
  if (gphMode==='select') { gphSelected=gphHitTest(cx,cy); gphRedraw(); return; }
  const pt=gphSnapPt(cx,cy);
  gphPending.push(pt);
  if (gphPending.length >= (GPH_POINT_NEEDED[gphMode]||1)) {
    gphHistory.push(JSON.parse(JSON.stringify(gphElements)));
    if (gphMode==='circle') {
      const r=Math.sqrt((gphPending[1][0]-gphPending[0][0])**2+(gphPending[1][1]-gphPending[0][1])**2);
      gphElements.push({type:'circle',pts:[gphPending[0]],r});
    } else {
      gphElements.push({type:gphMode,pts:[...gphPending]});
    }
    gphPending=[]; gphSerialise();
  }
  gphRedraw();
}

function gphHitTest(cx,cy) {
  for (let i=gphElements.length-1;i>=0;i--) {
    const el=gphElements[i];
    for (const pt of el.pts) {
      const [px,py]=gphToCanvas(...pt);
      if (Math.hypot(px-cx,py-cy)<10) return i;
    }
    if (el.type==='circle') {
      const [pcx,pcy]=gphToCanvas(...el.pts[0]);
      const r=Math.abs(gphToCanvas(el.pts[0][0]+el.r,el.pts[0][1])[0]-pcx);
      if (Math.abs(Math.hypot(pcx-cx,pcy-cy)-r)<10) return i;
    }
  }
  return null;
}

function gphOnMouseDown(e) {
  if (gphMode!=='select') return;
  const [cx,cy]=gphGetPos(e);
  const idx=gphHitTest(cx,cy);
  if (idx===null) return;
  let ptIdx=0, best=Infinity;
  gphElements[idx].pts.forEach((pt,i) => {
    const [px,py]=gphToCanvas(...pt);
    const d=Math.hypot(px-cx,py-cy);
    if(d<best){best=d;ptIdx=i;}
  });
  gphDragging={idx,ptIdx}; gphSelected=idx;
}
function gphOnMouseMove(e) {
  if (!gphDragging) return;
  const [cx,cy]=gphGetPos(e);
  const [gx,gy]=gphSnapPt(cx,cy);
  gphElements[gphDragging.idx].pts[gphDragging.ptIdx]=[gx,gy];
  gphRedraw();
}
function gphOnMouseUp() {
  if (gphDragging) { gphSerialise(); gphDragging=null; }
}

function gphDeleteSelected() {
  if (gphSelected===null) return;
  gphHistory.push(JSON.parse(JSON.stringify(gphElements)));
  gphElements.splice(gphSelected,1);
  gphSelected=null; gphSerialise(); gphRedraw();
}
function gphUndo() {
  if (!gphHistory.length) return;
  gphElements=gphHistory.pop();
  gphSelected=null; gphSerialise(); gphRedraw();
}
function gphSerialise() {
  document.getElementById('graphStateHidden').value = JSON.stringify({
    xMin:gphXMin,xMax:gphXMax,yMin:gphYMin,yMax:gphYMax,
    elements:gphElements
  });
}
function applyGraphSettings() {
  gphXMin=+document.getElementById('gph-xMin').value||-10;
  gphXMax=+document.getElementById('gph-xMax').value|| 10;
  gphYMin=+document.getElementById('gph-yMin').value||-10;
  gphYMax=+document.getElementById('gph-yMax').value|| 10;
  const plane=document.getElementById('gph-planeType').value;
  if(plane==='q1')    {gphXMin=0;gphYMin=0;}
  if(plane==='pos-x') {gphXMin=0;}
  document.getElementById('gph-xMin').value=gphXMin;
  document.getElementById('gph-yMin').value=gphYMin;
  gphSerialise(); gphRedraw();
}

// ════════════════════════════════════════════
//  GEOMETRY DRAWING ENGINE
// ════════════════════════════════════════════
let geoCtx, geoCanvas, geoW, geoH;
let geoGridSize = 20;           // px per cell
let geoMode     = 'select';
let geoElements = [];
let geoHistory  = [];
let geoSelected = null;
let geoPending  = [];
let geoDragging = null;

const GEO_COLOR        = '#696cff';
const GEO_SEL_COLOR    = '#ff6b6b';
const GEO_POINT_NEEDED = { point:1, segment:2, line:2, ray:2, circle:2 };

function geoInit() {
  geoCanvas = document.getElementById('geoCanvas');
  if (!geoCanvas) return;
  if (geoCanvas._ready) { geoRedraw(); return; }
  geoCanvas._ready = true;

  const panel = document.getElementById('panelGeometry');
  const sw    = panel.querySelector('.flex-grow-1').clientWidth || 480;
  const size  = Math.min(Math.max(sw - 20, 300), 540);
  geoCanvas.width  = size;
  geoCanvas.height = size;
  geoCanvas.style.width  = size + 'px';
  geoCanvas.style.height = size + 'px';
  geoW = geoH = size;
  geoCtx = geoCanvas.getContext('2d');

  document.querySelectorAll('.geo-mode-btn').forEach(b => {
    b.addEventListener('click', () => {
      geoMode = b.dataset.mode;
      document.querySelectorAll('.geo-mode-btn').forEach(x => x.classList.remove('active'));
      b.classList.add('active');
      geoPending = [];
      geoRedraw();
    });
  });

  geoCanvas.addEventListener('click',      geoOnClick);
  geoCanvas.addEventListener('mousedown',  geoOnMouseDown);
  geoCanvas.addEventListener('mousemove',  geoOnMouseMove);
  geoCanvas.addEventListener('mouseup',    geoOnMouseUp);
  geoCanvas.addEventListener('mouseleave', geoOnMouseUp);
  geoRedraw();
}

// ── coord helpers ──────────────────────────────────────────────────────────
function geoCols() { return Math.floor(geoW / geoGridSize); }
function geoRows() { return Math.floor(geoH / geoGridSize); }

// canvas px → grid cell (0-based)
function geoPxToCell(cx, cy) {
  return [Math.round(cx / geoGridSize), Math.round(cy / geoGridSize)];
}
// grid cell → canvas px (centre of cell)
function geoCellToPx(col, row) {
  return [col * geoGridSize, row * geoGridSize];
}
function geoGetPos(e) {
  const r  = geoCanvas.getBoundingClientRect();
  const sx = geoCanvas.width  / r.width;
  const sy = geoCanvas.height / r.height;
  return [(e.clientX - r.left) * sx, (e.clientY - r.top) * sy];
}

// ── draw ───────────────────────────────────────────────────────────────────
function geoRedraw() {
  if (!geoCtx) return;
  const c = geoCtx;
  c.clearRect(0, 0, geoW, geoH);

  // white background
  c.fillStyle = '#fff';
  c.fillRect(0, 0, geoW, geoH);

  // grid lines
  c.strokeStyle = '#dce0f5';
  c.lineWidth   = 1;
  const cols = geoCols(), rows = geoRows();
  for (let col = 0; col <= cols; col++) {
    const x = col * geoGridSize;
    c.beginPath(); c.moveTo(x, 0); c.lineTo(x, geoH); c.stroke();
  }
  for (let row = 0; row <= rows; row++) {
    const y = row * geoGridSize;
    c.beginPath(); c.moveTo(0, y); c.lineTo(geoW, y); c.stroke();
  }

  // border
  c.strokeStyle = '#c2c7e8';
  c.lineWidth   = 1.5;
  c.strokeRect(0, 0, geoW, geoH);

  // elements
  geoElements.forEach((el, i) => geoDrawElement(c, el, i === geoSelected));

  // pending ghost points
  geoPending.forEach(pt => {
    const [px, py] = geoCellToPx(...pt);
    c.beginPath(); c.arc(px, py, 4, 0, Math.PI * 2);
    c.fillStyle = 'rgba(105,108,255,0.45)'; c.fill();
  });
}

function geoDrawElement(c, el, selected) {
  const col = selected ? GEO_SEL_COLOR : GEO_COLOR;
  c.strokeStyle = col; c.fillStyle = col; c.lineWidth = 2;

  if (el.type === 'point') {
    const [px, py] = geoCellToPx(...el.pts[0]);
    c.beginPath(); c.arc(px, py, 6, 0, Math.PI * 2); c.fill();
    c.fillStyle = '#fff';
    c.beginPath(); c.arc(px, py, 3, 0, Math.PI * 2); c.fill();

  } else if (el.type === 'segment') {
    const [x1, y1] = geoCellToPx(...el.pts[0]);
    const [x2, y2] = geoCellToPx(...el.pts[1]);
    c.beginPath(); c.moveTo(x1, y1); c.lineTo(x2, y2); c.stroke();
    geoDot(c, x1, y1, col); geoDot(c, x2, y2, col);

  } else if (el.type === 'line') {
    const [x1, y1] = geoCellToPx(...el.pts[0]);
    const [x2, y2] = geoCellToPx(...el.pts[1]);
    const dx = x2 - x1, dy = y2 - y1, ext = 4000;
    c.beginPath(); c.moveTo(x1 - dx * ext, y1 - dy * ext); c.lineTo(x1 + dx * ext, y1 + dy * ext); c.stroke();
    geoDot(c, x1, y1, col); geoDot(c, x2, y2, col);

  } else if (el.type === 'ray') {
    const [x1, y1] = geoCellToPx(...el.pts[0]);
    const [x2, y2] = geoCellToPx(...el.pts[1]);
    const dx = x2 - x1, dy = y2 - y1, ang = Math.atan2(dy, dx);
    c.beginPath(); c.moveTo(x1, y1); c.lineTo(x1 + dx * 4000, y1 + dy * 4000); c.stroke();
    geoDot(c, x1, y1, col);
    // arrowhead
    const tip = 20;
    c.beginPath();
    c.moveTo(x2 + dx * 4, y2 + dy * 4);
    c.lineTo(x2 + dx * 4 - tip * Math.cos(ang - 0.4), y2 + dy * 4 - tip * Math.sin(ang - 0.4));
    c.lineTo(x2 + dx * 4 - tip * Math.cos(ang + 0.4), y2 + dy * 4 - tip * Math.sin(ang + 0.4));
    c.closePath(); c.fill();

  } else if (el.type === 'circle') {
    const [cx, cy] = geoCellToPx(...el.pts[0]);
    const [rx, ry] = geoCellToPx(...el.pts[1]);
    const r = Math.hypot(rx - cx, ry - cy);
    c.beginPath(); c.arc(cx, cy, r, 0, Math.PI * 2); c.stroke();
    geoDot(c, cx, cy, col);
  }
}

function geoDot(c, px, py, col) {
  c.fillStyle = col;
  c.beginPath(); c.arc(px, py, 5, 0, Math.PI * 2); c.fill();
  c.fillStyle = '#fff';
  c.beginPath(); c.arc(px, py, 2.5, 0, Math.PI * 2); c.fill();
}

// ── events ─────────────────────────────────────────────────────────────────
function geoOnClick(e) {
  if (geoDragging) return;
  const [cx, cy] = geoGetPos(e);
  if (geoMode === 'select') { geoSelected = geoHitTest(cx, cy); geoRedraw(); return; }
  const cell = geoPxToCell(cx, cy);
  geoPending.push(cell);
  if (geoPending.length >= (GEO_POINT_NEEDED[geoMode] || 1)) {
    geoHistory.push(JSON.parse(JSON.stringify(geoElements)));
    geoElements.push({ type: geoMode, pts: [...geoPending] });
    geoPending = [];
    geoSerialise();
  }
  geoRedraw();
}

function geoHitTest(cx, cy) {
  for (let i = geoElements.length - 1; i >= 0; i--) {
    for (const pt of geoElements[i].pts) {
      const [px, py] = geoCellToPx(...pt);
      if (Math.hypot(px - cx, py - cy) < 10) return i;
    }
    if (geoElements[i].type === 'circle') {
      const [pcx, pcy] = geoCellToPx(...geoElements[i].pts[0]);
      const [prx, pry] = geoCellToPx(...geoElements[i].pts[1]);
      const r = Math.hypot(prx - pcx, pry - pcy);
      if (Math.abs(Math.hypot(pcx - cx, pcy - cy) - r) < 10) return i;
    }
  }
  return null;
}

function geoOnMouseDown(e) {
  if (geoMode !== 'select') return;
  const [cx, cy] = geoGetPos(e);
  const idx = geoHitTest(cx, cy);
  if (idx === null) return;
  let ptIdx = 0, best = Infinity;
  geoElements[idx].pts.forEach((pt, i) => {
    const [px, py] = geoCellToPx(...pt);
    const d = Math.hypot(px - cx, py - cy);
    if (d < best) { best = d; ptIdx = i; }
  });
  geoDragging = { idx, ptIdx }; geoSelected = idx;
}
function geoOnMouseMove(e) {
  if (!geoDragging) return;
  const [cx, cy] = geoGetPos(e);
  geoElements[geoDragging.idx].pts[geoDragging.ptIdx] = geoPxToCell(cx, cy);
  geoRedraw();
}
function geoOnMouseUp() {
  if (geoDragging) { geoSerialise(); geoDragging = null; }
}

function geoDeleteSelected() {
  if (geoSelected === null) return;
  geoHistory.push(JSON.parse(JSON.stringify(geoElements)));
  geoElements.splice(geoSelected, 1);
  geoSelected = null; geoSerialise(); geoRedraw();
}
function geoUndo() {
  if (!geoHistory.length) return;
  geoElements = geoHistory.pop();
  geoSelected = null; geoSerialise(); geoRedraw();
}
function geoSerialise() {
  document.getElementById('geoStateHidden').value = JSON.stringify({ gridSize: geoGridSize, elements: geoElements });
}
function applyGeoSettings() {
  geoGridSize = Math.max(5, +document.getElementById('geo-gridSize').value || 20);
  geoSerialise(); geoRedraw();
}

// ════════════════════════════════════════════
//  NUMBER LINE ENGINE
// ════════════════════════════════════════════
let nlCtx, nlCanvas, nlW, nlH;
let nlMin  = -10, nlMax = 10, nlStep = 2;
let nlMode = 'select';
let nlElements = [];   // [{type:'point',x}, {type:'segment',x1,x2}]
let nlHistory  = [];
let nlSelected = null;
let nlPending  = [];   // for segment: accumulates 2 clicks
let nlDragging = null; // {idx, which:'x'|'x1'|'x2'}

function nlInit() {
  nlCanvas = document.getElementById('nlCanvas');
  if (!nlCanvas) return;
  if (nlCanvas._ready) { nlRedraw(); return; }
  nlCanvas._ready = true;

  const panel = document.getElementById('panelNumberLine');
  const sw    = panel.querySelector('.flex-grow-1').clientWidth || 480;
  nlW = Math.max(sw - 24, 280);
  nlH = 120;
  nlCanvas.width  = nlW;
  nlCanvas.height = nlH;
  nlCanvas.style.width  = nlW + 'px';
  nlCanvas.style.height = nlH + 'px';
  nlCtx = nlCanvas.getContext('2d');

  // mode buttons
  document.querySelectorAll('.nl-mode-btn').forEach(b => {
    b.addEventListener('click', () => {
      nlMode = b.dataset.mode;
      document.querySelectorAll('.nl-mode-btn').forEach(x => x.classList.remove('active'));
      b.classList.add('active');
      nlPending = [];
      nlRedraw();
    });
  });

  nlCanvas.addEventListener('click',      nlOnClick);
  nlCanvas.addEventListener('mousedown',  nlOnMouseDown);
  nlCanvas.addEventListener('mousemove',  nlOnMouseMove);
  nlCanvas.addEventListener('mouseup',    nlOnMouseUp);
  nlCanvas.addEventListener('mouseleave', nlOnMouseUp);
  nlRedraw();
}

// ── coordinate helpers ─────────────────────
function nlToCanvas(v) {
  const PAD = 30;
  return PAD + (v - nlMin) / (nlMax - nlMin) * (nlW - PAD * 2);
}
function nlToValue(px) {
  const PAD = 30;
  const raw = (px - PAD) / (nlW - PAD * 2) * (nlMax - nlMin) + nlMin;
  // snap to nearest step/4
  return Math.round(raw / (nlStep / 4)) * (nlStep / 4);
}
function nlGetPos(e) {
  const r  = nlCanvas.getBoundingClientRect();
  const sx = nlCanvas.width / r.width;
  return (e.clientX - r.left) * sx;
}

// ── draw ───────────────────────────────────
function nlRedraw() {
  if (!nlCtx) return;
  const c   = nlCtx;
  const CY  = nlH / 2;      // centre y of the line
  const PAD = 30;

  c.clearRect(0, 0, nlW, nlH);
  c.fillStyle = '#fff';
  c.fillRect(0, 0, nlW, nlH);

  // ── axis line ──
  c.strokeStyle = '#5c6079';
  c.lineWidth   = 1.8;
  c.beginPath();
  c.moveTo(PAD - 8, CY);
  c.lineTo(nlW - PAD + 8, CY);
  c.stroke();

  // left arrowhead
  c.fillStyle = '#5c6079';
  nlArrow(c, nlW - PAD + 8, CY, PAD - 8, CY);
  // right arrowhead
  nlArrow(c, PAD - 8, CY, nlW - PAD + 8, CY);

  // ── ticks + labels ──
  c.strokeStyle = '#5c6079';
  c.lineWidth   = 1.2;
  c.fillStyle   = '#5c6079';
  c.font        = '11px system-ui';
  c.textAlign   = 'center';

  const MAJOR_H = 10, MINOR_H = 5;
  // minor ticks at every nlStep/2
  const minor = nlStep / 2;
  for (let v = nlMin; v <= nlMax + 1e-9; v += minor) {
    const px = nlToCanvas(v);
    const isMajor = Math.abs(Math.round(v / nlStep) * nlStep - v) < 1e-9;
    const h = isMajor ? MAJOR_H : MINOR_H;
    c.beginPath(); c.moveTo(px, CY - h); c.lineTo(px, CY + h); c.stroke();
    if (isMajor) c.fillText(+v.toFixed(8), px, CY + h + 14);
  }

  // ── elements ──
  nlElements.forEach((el, i) => nlDrawElement(c, el, i === nlSelected, CY));

  // pending ghost (first click of segment)
  if (nlPending.length === 1) {
    const px = nlToCanvas(nlPending[0]);
    c.beginPath(); c.arc(px, CY, 6, 0, Math.PI * 2);
    c.fillStyle = 'rgba(105,108,255,0.45)'; c.fill();
  }
}

function nlArrow(c, fx, fy, tx, ty) {
  const ang = Math.atan2(ty - fy, tx - fx), len = 8;
  c.beginPath();
  c.moveTo(tx, ty);
  c.lineTo(tx - len * Math.cos(ang - 0.4), ty - len * Math.sin(ang - 0.4));
  c.lineTo(tx - len * Math.cos(ang + 0.4), ty - len * Math.sin(ang + 0.4));
  c.closePath(); c.fill();
}

function nlDrawElement(c, el, selected, cy) {
  const COL     = selected ? '#ff6b6b' : '#696cff';
  const DOT_R   = 6;

  if (el.type === 'point') {
    const px = nlToCanvas(el.x);
    // filled circle with white centre
    c.fillStyle = COL;
    c.beginPath(); c.arc(px, cy, DOT_R, 0, Math.PI * 2); c.fill();
    c.fillStyle = '#fff';
    c.beginPath(); c.arc(px, cy, 3,     0, Math.PI * 2); c.fill();

  } else if (el.type === 'segment') {
    const x1 = nlToCanvas(el.x1), x2 = nlToCanvas(el.x2);
    // line between endpoints
    c.strokeStyle = COL; c.lineWidth = 2.5;
    c.beginPath(); c.moveTo(x1, cy); c.lineTo(x2, cy); c.stroke();
    // endpoint dots
    [x1, x2].forEach(px => {
      c.fillStyle = COL;
      c.beginPath(); c.arc(px, cy, DOT_R, 0, Math.PI * 2); c.fill();
      c.fillStyle = '#fff';
      c.beginPath(); c.arc(px, cy, 3,     0, Math.PI * 2); c.fill();
    });
  }
}

// ── events ─────────────────────────────────
function nlOnClick(e) {
  if (nlDragging) return;
  const cx = nlGetPos(e);
  const v  = nlToValue(cx);

  if (nlMode === 'select') {
    nlSelected = nlHitTest(cx);
    nlRedraw();
    return;
  }

  if (nlMode === 'point') {
    nlHistory.push(JSON.parse(JSON.stringify(nlElements)));
    nlElements.push({ type: 'point', x: v });
    nlSerialise(); nlRedraw();
    return;
  }

  if (nlMode === 'segment') {
    nlPending.push(v);
    if (nlPending.length === 2) {
      nlHistory.push(JSON.parse(JSON.stringify(nlElements)));
      const [a, b] = nlPending.sort((x, y) => x - y);
      nlElements.push({ type: 'segment', x1: a, x2: b });
      nlPending = [];
      nlSerialise();
    }
    nlRedraw();
  }
}

function nlHitTest(cx) {
  const CY = nlH / 2, HIT = 12;
  for (let i = nlElements.length - 1; i >= 0; i--) {
    const el = nlElements[i];
    if (el.type === 'point') {
      if (Math.abs(nlToCanvas(el.x) - cx) < HIT) return i;
    } else if (el.type === 'segment') {
      if (Math.abs(nlToCanvas(el.x1) - cx) < HIT) return i;
      if (Math.abs(nlToCanvas(el.x2) - cx) < HIT) return i;
      const x1 = nlToCanvas(el.x1), x2 = nlToCanvas(el.x2);
      if (cx > Math.min(x1,x2) - HIT && cx < Math.max(x1,x2) + HIT) return i;
    }
  }
  return null;
}

function nlOnMouseDown(e) {
  if (nlMode !== 'select') return;
  const cx  = nlGetPos(e);
  const idx = nlHitTest(cx);
  if (idx === null) return;
  const el = nlElements[idx];
  let which = 'x';
  if (el.type === 'segment') {
    const d1 = Math.abs(nlToCanvas(el.x1) - cx);
    const d2 = Math.abs(nlToCanvas(el.x2) - cx);
    which = d1 < d2 ? 'x1' : 'x2';
  }
  nlDragging = { idx, which }; nlSelected = idx;
}
function nlOnMouseMove(e) {
  if (!nlDragging) return;
  const v = nlToValue(nlGetPos(e));
  const el = nlElements[nlDragging.idx];
  el[nlDragging.which] = v;
  if (el.type === 'segment' && el.x1 > el.x2) {
    [el.x1, el.x2] = [el.x2, el.x1];
    nlDragging.which = nlDragging.which === 'x1' ? 'x2' : 'x1';
  }
  nlRedraw();
}
function nlOnMouseUp() {
  if (nlDragging) { nlSerialise(); nlDragging = null; }
}

function nlDeleteSelected() {
  if (nlSelected === null) return;
  nlHistory.push(JSON.parse(JSON.stringify(nlElements)));
  nlElements.splice(nlSelected, 1);
  nlSelected = null; nlSerialise(); nlRedraw();
}
function nlUndo() {
  if (!nlHistory.length) return;
  nlElements = nlHistory.pop();
  nlSelected = null; nlSerialise(); nlRedraw();
}
function nlSerialise() {
  const el = document.getElementById('nlStateHidden');
  if (el) el.value = JSON.stringify({ min: nlMin, max: nlMax, step: nlStep, elements: nlElements });
}
function applyNlSettings() {
  nlMin  = +document.getElementById('nl-min').value;
  nlMax  = +document.getElementById('nl-max').value;
  nlStep = +document.getElementById('nl-step').value || 1;
  if (nlMin >= nlMax) { nlMax = nlMin + nlStep; document.getElementById('nl-max').value = nlMax; }
  nlSerialise(); nlRedraw();
}

// ════════════════════════════════════════════
//  DOT PLOT ENGINE
// ════════════════════════════════════════════
let dpCtx, dpCanvas;
let dpColSize  = 10;          // max rows per column
let dpLabelStep = 1;          // label every N ticks
let dpLabelText = '';         // axis title (unused visually but stored)
let dpItemType  = 'cross';    // 'cross' | 'dot'
let dpTicks     = [0,1,2,3,4]; // labeled tick values
// grid state: dpGrid[colIdx] = count of items placed
let dpGrid = [];

// layout constants (computed in dpApplySettings)
let dpCellSize = 36;   // px per grid cell
let dpPadL     = 30;   // left padding
let dpPadR     = 30;   // right padding
let dpPadT     = 12;   // top padding
let dpAxisY    = 0;    // y of the axis line (set in draw)

function dpInit() {
  dpCanvas = document.getElementById('dpCanvas');
  if (!dpCanvas) return;
  dpCanvas._ready = true;
  dpCtx = dpCanvas.getContext('2d');
  dpApplySettings();

  dpCanvas.addEventListener('click', dpOnClick);
}

function dpApplySettings() {
  dpColSize   = Math.max(1, +document.getElementById('dp-colSize').value   || 10);
  dpLabelStep = Math.max(1, +document.getElementById('dp-labelStep').value || 1);
  dpLabelText = document.getElementById('dp-label').value || '';
  dpItemType  = document.getElementById('dp-itemType').value || 'cross';

  const raw   = document.getElementById('dp-ticks').value || '';
  dpTicks     = raw.split(';').map(s => s.trim()).filter(s => s !== '').map(Number).filter(n => !isNaN(n));
  if (!dpTicks.length) dpTicks = [0,1,2,3,4];

  // reset grid to match tick count
  dpGrid = dpTicks.map(() => 0);

  dpSizeCanvas();
  dpSerialise();
  dpRedraw();
}

function dpSizeCanvas() {
  const cols = dpTicks.length;
  dpCellSize = 36;
  const W = dpPadL + cols * dpCellSize + dpPadR;
  const H = dpPadT + dpColSize * dpCellSize + 60; // 60 = axis + label room
  dpCanvas.width  = W;
  dpCanvas.height = H;
  dpCanvas.style.width  = W + 'px';
  dpCanvas.style.height = H + 'px';
  dpAxisY = dpPadT + dpColSize * dpCellSize;
}

function dpRedraw() {
  if (!dpCtx) return;
  const c   = dpCtx;
  const W   = dpCanvas.width, H = dpCanvas.height;
  const cols = dpTicks.length;

  c.clearRect(0, 0, W, H);
  c.fillStyle = '#fff';
  c.fillRect(0, 0, W, H);

  // ── ghost placeholders (empty cells) ──
  for (let col = 0; col < cols; col++) {
    const cx = dpPadL + col * dpCellSize + dpCellSize / 2;
    for (let row = 0; row < dpColSize; row++) {
      const cy = dpAxisY - (row + 1) * dpCellSize + dpCellSize / 2;
      dpDrawItem(c, cx, cy, dpItemType, true, false);
    }
  }

  // ── placed items ──
  for (let col = 0; col < cols; col++) {
    const cx = dpPadL + col * dpCellSize + dpCellSize / 2;
    for (let row = 0; row < dpGrid[col]; row++) {
      const cy = dpAxisY - (row + 1) * dpCellSize + dpCellSize / 2;
      dpDrawItem(c, cx, cy, dpItemType, false, false);
    }
  }

  // ── axis line with arrows ──
  const axL = dpPadL - 8, axR = dpPadL + cols * dpCellSize + 8;
  c.strokeStyle = '#5c6079'; c.lineWidth = 1.8;
  c.beginPath(); c.moveTo(axL, dpAxisY); c.lineTo(axR, dpAxisY); c.stroke();
  // arrowheads
  dpArrow(c, axR, dpAxisY, true);
  dpArrow(c, axL, dpAxisY, false);

  // ── ticks + labels ──
  c.strokeStyle = '#5c6079'; c.lineWidth = 1.2;
  c.fillStyle   = '#5c6079'; c.font = '11px system-ui'; c.textAlign = 'center';
  for (let i = 0; i < cols; i++) {
    const px = dpPadL + i * dpCellSize + dpCellSize / 2;
    // tick
    c.beginPath(); c.moveTo(px, dpAxisY - 5); c.lineTo(px, dpAxisY + 5); c.stroke();
    // label every dpLabelStep columns
    if (i % dpLabelStep === 0) c.fillText(dpTicks[i], px, dpAxisY + 20);
  }

  // axis title
  if (dpLabelText) {
    c.fillStyle = '#5c6079'; c.font = '12px system-ui'; c.textAlign = 'center';
    c.fillText(dpLabelText, W / 2, H - 4);
  }
}

function dpArrow(c, px, py, right) {
  const len = 7, spread = 0.38;
  const dir = right ? 1 : -1;
  c.fillStyle = '#5c6079';
  c.beginPath();
  c.moveTo(px, py);
  c.lineTo(px - dir * len * Math.cos(-spread), py - len * Math.sin(-spread));
  c.lineTo(px - dir * len * Math.cos( spread), py - len * Math.sin( spread));
  c.closePath(); c.fill();
}

function dpDrawItem(c, cx, cy, type, ghost, selected) {
  const alpha = ghost ? 0.18 : 1;
  const col   = selected ? '#ff6b6b' : '#8a8fc8';
  c.globalAlpha = alpha;
  if (type === 'dot') {
    c.fillStyle = col;
    c.beginPath(); c.arc(cx, cy, 7, 0, Math.PI * 2); c.fill();
    if (!ghost) {
      c.fillStyle = '#fff';
      c.beginPath(); c.arc(cx, cy, 3, 0, Math.PI * 2); c.fill();
    }
  } else {
    // cross / X
    const s = 7;
    c.strokeStyle = col; c.lineWidth = 2; c.lineCap = 'round';
    c.beginPath(); c.moveTo(cx - s, cy - s); c.lineTo(cx + s, cy + s); c.stroke();
    c.beginPath(); c.moveTo(cx + s, cy - s); c.lineTo(cx - s, cy + s); c.stroke();
  }
  c.globalAlpha = 1;
}

function dpOnClick(e) {
  const r  = dpCanvas.getBoundingClientRect();
  const sx = dpCanvas.width  / r.width;
  const sy = dpCanvas.height / r.height;
  const cx = (e.clientX - r.left) * sx;
  const cy = (e.clientY - r.top)  * sy;

  // which column? (centre-aligned cells)
  const col = Math.floor((cx - dpPadL) / dpCellSize);
  if (col < 0 || col >= dpTicks.length) return;

  // which row from the top (0 = topmost ghost cell)
  const rowFromTop = Math.floor((cy - dpPadT) / dpCellSize);
  if (rowFromTop < 0 || rowFromTop >= dpColSize) return;

  // convert to "row from bottom" (0 = row just above axis)
  const rowFromBase = dpColSize - 1 - rowFromTop; // 0..dpColSize-1

  const filled = dpGrid[col]; // how many items are stacked (counted from bottom)

  if (rowFromBase < filled) {
    // clicked ON a filled item → remove the top-most item
    dpGrid[col] = rowFromBase + 1;
  } else {
    // clicked on an empty ghost → fill up to this row
    dpGrid[col] = Math.min(dpColSize, rowFromBase + 1);
  }

  dpSerialise();
  dpRedraw();
}

function dpSerialise() {
  const el = document.getElementById('dpStateHidden');
  if (el) el.value = JSON.stringify({
    colSize: dpColSize, labelStep: dpLabelStep, label: dpLabelText,
    itemType: dpItemType, ticks: dpTicks, grid: dpGrid
  });
}

// ════════════════════════════════════════════
//  PROTRACTOR
// ════════════════════════════════════════════
// protAngle: 0 = pointing right (east, 0° on protractor)
//            180 = pointing left (west, 180° on protractor)
let protCanvas   = null;
let protCtx      = null;
let protAngle    = 0;      // 0–180 degrees, matching protractor scale
let protDragging = false;

function protInit() {
  protCanvas = document.getElementById('protCanvas');
  if (!protCanvas) return;

  // Size: width fills card, height = semicircle R + base padding
  const wrap = protCanvas.closest('.d-flex.justify-content-center');
  const cardBody = document.getElementById('panelProtractor')?.querySelector('.card-body');
  const W = Math.min(Math.max((cardBody ? cardBody.clientWidth : 580) - 32, 400), 660);
  const R = W * 0.43;
  // Canvas height: R (semicircle) + 60px base area (handle + labels)
  const H = Math.round(R + 68);
  protCanvas.width  = W;
  protCanvas.height = H;
  protCtx = protCanvas.getContext('2d');

  protAngle = 0;

  // Re-draw on settings change
  const precEl = document.getElementById('protPrecision');
  const dirEl  = document.getElementById('protDirection');
  if (precEl && !precEl._protBound) { precEl.addEventListener('input', protRedraw); precEl._protBound = true; }
  if (dirEl  && !dirEl._protBound)  { dirEl.addEventListener('change', protRedraw);  dirEl._protBound  = true; }

  // Drag to rotate arrow
  if (!protCanvas._protBound) {
    protCanvas.addEventListener('mousedown',  protOnDown);
    protCanvas.addEventListener('mousemove',  protOnMove);
    protCanvas.addEventListener('mouseup',    protOnUp);
    protCanvas.addEventListener('mouseleave', protOnUp);
    protCanvas.addEventListener('touchstart', protOnTouchStart, { passive: true });
    protCanvas.addEventListener('touchmove',  protOnTouchMove,  { passive: false });
    protCanvas.addEventListener('touchend',   protOnUp);
    protCanvas._protBound = true;
  }

  protRedraw();
}

// ── helpers ───────────────────────────────────────────────────────────────
// Centre of the protractor (flat edge sits at cy, semicircle arcs upward)
function protGeom() {
  const W = protCanvas.width, H = protCanvas.height;
  const R = W * 0.43;
  // flat edge baseline: leave 50px below for handle/label
  const cy = H - 50;
  const cx = W / 2;
  return { cx, cy, R };
}

function protSnap(deg) {
  const prec = Math.max(1, parseFloat(document.getElementById('protPrecision')?.value) || 5);
  return Math.min(180, Math.max(0, Math.round(deg / prec) * prec));
}

// Convert canvas mouse position → protractor degrees (0=right, 180=left)
function protDegFromEvent(e) {
  const rect  = protCanvas.getBoundingClientRect();
  const sx    = protCanvas.width  / rect.width;
  const sy    = protCanvas.height / rect.height;
  const px    = (e.clientX - rect.left) * sx;
  const py    = (e.clientY - rect.top)  * sy;
  const { cx, cy } = protGeom();
  // atan2: angle from east, positive = above baseline
  let deg = Math.atan2(cy - py, px - cx) * 180 / Math.PI;
  return Math.max(0, Math.min(180, deg));
}

// ── draw ──────────────────────────────────────────────────────────────────
function protRedraw() {
  if (!protCtx) return;
  const c = protCtx;
  const W = protCanvas.width, H = protCanvas.height;
  c.clearRect(0, 0, W, H);

  const { cx, cy, R } = protGeom();
  const snapped = protSnap(protAngle);

  // ── light-blue angle sweep ──────────────────────────────────────────────
  // Arrow is at protractor angle `snapped`: 0°=east, 180°=west
  // On canvas: east = angle 0, the arc goes counter-clockwise (upward)
  if (snapped > 0) {
    const sweepStart = 0;                          // canvas east (0°)
    const sweepEnd   = -snapped * Math.PI / 180;   // upward (negative y)
    c.beginPath();
    c.moveTo(cx, cy);
    c.arc(cx, cy, R * 1.1, sweepStart, sweepEnd, true);
    c.closePath();
    c.fillStyle = 'rgba(173,210,245,0.4)';
    c.fill();
  }

  // ── protractor body ─────────────────────────────────────────────────────
  c.beginPath();
  c.arc(cx, cy, R, Math.PI, 0, false);  // semicircle top (left→right)
  c.lineTo(cx + R, cy);
  c.lineTo(cx - R, cy);
  c.closePath();
  c.fillStyle   = '#f8f9ff';
  c.fill();
  c.strokeStyle = '#1a1a2e';
  c.lineWidth   = 2;
  c.stroke();

  // inner cutout arc
  const innerR = R * 0.12;
  c.beginPath();
  c.arc(cx, cy, innerR, Math.PI, 0, false);
  c.strokeStyle = '#333';
  c.lineWidth   = 1.5;
  c.stroke();

  // ── tick marks & labels ──────────────────────────────────────────────────
  // Protractor convention:
  //   0°  = RIGHT  (east  = canvas angle 0)
  //   90° = TOP    (north = canvas angle -π/2)
  //   180°= LEFT   (west  = canvas angle -π or π)
  // Canvas angle for protractor degree d:  canvasAngle = -d * π/180  (negative = upward)
  const fontSize = Math.max(9, Math.round(R * 0.072));
  c.font = `${fontSize}px sans-serif`;
  c.textAlign    = 'center';
  c.textBaseline = 'middle';

  for (let d = 0; d <= 180; d++) {
    const canvasAngle = -d * Math.PI / 180;   // 0=east, going CCW upward
    const cosA = Math.cos(canvasAngle);
    const sinA = Math.sin(canvasAngle);

    // tick length
    let tickIn;
    if      (d % 10 === 0) tickIn = R * 0.86;
    else if (d % 5  === 0) tickIn = R * 0.90;
    else                    tickIn = R * 0.94;

    c.beginPath();
    c.moveTo(cx + cosA * tickIn, cy + sinA * tickIn);
    c.lineTo(cx + cosA * R,      cy + sinA * R);
    c.strokeStyle = '#222';
    c.lineWidth   = d % 10 === 0 ? 1.5 : (d % 5 === 0 ? 1.0 : 0.6);
    c.stroke();

    if (d % 10 === 0) {
      // Outer label (primary scale: 0–180 left-to-right as seen on real protractor)
      const outerLabelR = R * 0.78;
      const lx = cx + cosA * outerLabelR;
      const ly = cy + sinA * outerLabelR;
      c.fillStyle = '#111';
      c.save();
      c.translate(lx, ly);
      // Rotate text to be radially aligned, reading outward
      c.rotate(canvasAngle + Math.PI / 2);
      c.fillText(String(d), 0, 0);
      c.restore();

      // Inner label (secondary scale: 180–0)
      const inner2 = 180 - d;
      if (inner2 !== d) {
        const innerLabelR = R * 0.65;
        const lx2 = cx + cosA * innerLabelR;
        const ly2 = cy + sinA * innerLabelR;
        c.fillStyle = '#666';
        c.save();
        c.translate(lx2, ly2);
        c.rotate(canvasAngle + Math.PI / 2);
        c.fillText(String(inner2), 0, 0);
        c.restore();
      }
    }
  }

  // centre dot
  c.beginPath();
  c.arc(cx, cy, 4.5, 0, Math.PI * 2);
  c.fillStyle = '#222'; c.fill();

  // ── red arrow ────────────────────────────────────────────────────────────
  const arrowCanvasAngle = -snapped * Math.PI / 180;
  const ax = cx + Math.cos(arrowCanvasAngle) * (R * 0.88);
  const ay = cy + Math.sin(arrowCanvasAngle) * (R * 0.88);

  // shaft (extends slightly past baseline to the left for the handle line)
  c.beginPath();
  c.moveTo(cx, cy);
  c.lineTo(ax, ay);
  c.strokeStyle = '#cc0000';
  c.lineWidth   = 2;
  c.stroke();

  // arrowhead
  const hLen = R * 0.055, hAng = 0.38;
  c.beginPath();
  c.moveTo(ax, ay);
  c.lineTo(ax - hLen * Math.cos(arrowCanvasAngle - hAng), ay - hLen * Math.sin(arrowCanvasAngle - hAng));
  c.lineTo(ax - hLen * Math.cos(arrowCanvasAngle + hAng), ay - hLen * Math.sin(arrowCanvasAngle + hAng));
  c.closePath();
  c.fillStyle = '#cc0000'; c.fill();

  // small open circle near pivot
  const pivotDist = R * 0.08;
  const pvx = cx + Math.cos(arrowCanvasAngle) * pivotDist;
  const pvy = cy + Math.sin(arrowCanvasAngle) * pivotDist;
  c.beginPath();
  c.arc(pvx, pvy, R * 0.022, 0, Math.PI * 2);
  c.strokeStyle = '#cc0000'; c.lineWidth = 1.5; c.stroke();
  c.fillStyle = '#fff'; c.fill();

  // baseline extension to the right (0°) — thin red line from centre to right edge
  c.beginPath();
  c.moveTo(cx, cy);
  c.lineTo(cx + R, cy);
  c.strokeStyle = 'rgba(200,0,0,0.25)';
  c.lineWidth   = 1;
  c.stroke();

  // ── drag handle button ────────────────────────────────────────────────────
  // Positioned at the left side at baseline height
  const btnCx = cx - R - 28;
  const btnCy = cy;
  const btnR  = 18;

  c.beginPath();
  c.roundRect(btnCx - btnR, btnCy - btnR, btnR * 2, btnR * 2, 8);
  c.fillStyle = '#696cff';
  c.fill();

  // chevron up/down
  c.strokeStyle = '#fff'; c.lineWidth = 2.2; c.lineCap = 'round'; c.lineJoin = 'round';
  c.beginPath();
  c.moveTo(btnCx - 5, btnCy - 4); c.lineTo(btnCx, btnCy - 9); c.lineTo(btnCx + 5, btnCy - 4);
  c.stroke();
  c.beginPath();
  c.moveTo(btnCx - 5, btnCy + 4); c.lineTo(btnCx, btnCy + 9); c.lineTo(btnCx + 5, btnCy + 4);
  c.stroke();

  // arrow from button pointing right
  c.strokeStyle = '#cc0000'; c.lineWidth = 2; c.lineCap = 'butt';
  c.beginPath();
  c.moveTo(btnCx + btnR + 2, btnCy);
  c.lineTo(cx - R, cy);
  c.stroke();
  // small arrowhead on that line pointing right
  c.beginPath();
  c.moveTo(btnCx + btnR + 10, btnCy);
  c.lineTo(btnCx + btnR + 4,  btnCy - 4);
  c.lineTo(btnCx + btnR + 4,  btnCy + 4);
  c.closePath();
  c.fillStyle = '#cc0000'; c.fill();

  // ── degree readout ────────────────────────────────────────────────────────
  c.font      = `bold ${Math.round(R * 0.085)}px sans-serif`;
  c.fillStyle = '#cc0000';
  c.textAlign = 'left';
  c.textBaseline = 'middle';
  c.fillText(`${snapped}°`, btnCx + btnR + 14, btnCy - R * 0.0);

  protSerialise();
}

// ── interaction ───────────────────────────────────────────────────────────
function protOnDown(e) {
  protDragging = true;
  protAngle = protDegFromEvent(e);
  protRedraw();
}
function protOnMove(e) {
  if (!protDragging) return;
  protAngle = protDegFromEvent(e);
  protRedraw();
}
function protOnUp() { protDragging = false; }

function protOnTouchStart(e) {
  protDragging = true;
  protAngle = protDegFromEvent(e.touches[0]);
  protRedraw();
}
function protOnTouchMove(e) {
  e.preventDefault();
  if (!protDragging) return;
  protAngle = protDegFromEvent(e.touches[0]);
  protRedraw();
}

function protSerialise() {
  const el = document.getElementById('protStateHidden');
  if (!el) return;
  el.value = JSON.stringify({
    angle:     protSnap(protAngle),
    precision: Math.max(1, parseFloat(document.getElementById('protPrecision')?.value) || 5),
    direction: document.getElementById('protDirection')?.value || 'cw'
  });
}