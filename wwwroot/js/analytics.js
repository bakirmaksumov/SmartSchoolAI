// Analytics page: filters apply on change, the status donut, student search, question answers and the CSV export.
// Everything that matters is a normal link or form in the page, so it also works without this script.
(function () {
  'use strict';
  var page = document.getElementById('analyticsPage');
  if (!page) return;

  // Filters: a change reloads with the new choice in the address (Back returns to the previous view).
  // Defaults (all types, any time, the overview) are left out, so addresses stay short and match the page's own links.
  document.querySelectorAll('[data-autosubmit]').forEach(function (el) {
    el.addEventListener('change', function () {
      var params = new URLSearchParams();
      new FormData(el.form).forEach(function (value, key) {
        if (value === '' || value === 'all' || (key === 'tab' && value === 'overview')) return;
        params.append(key, value);
      });
      location.href = el.form.getAttribute('action') + (params.toString() ? '?' + params.toString() : '');
    });
  });

  // Submission status donut (ApexCharts, already used in this project). A segment opens the same list as its legend line.
  var donut = document.getElementById('statusDonut');
  if (donut && window.ApexCharts) {
    var series = JSON.parse(donut.dataset.series), labels = JSON.parse(donut.dataset.labels);
    var colors = JSON.parse(donut.dataset.colors), links = JSON.parse(donut.dataset.links);
    var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    var total = parseInt(donut.dataset.total || '0', 10);
    new ApexCharts(donut, {
      chart: {
        type: 'donut', height: donut.offsetWidth || 220, animations: { enabled: !reduce, speed: 200 },
        events: { dataPointSelection: function (e, ctx, cfg) { if (links[cfg.dataPointIndex]) location.href = links[cfg.dataPointIndex]; } }
      },
      series: series, labels: labels, colors: colors,
      legend: { show: false }, dataLabels: { enabled: true, formatter: function (v, o) { return o.w.config.series[o.seriesIndex] || ''; } },
      stroke: { width: 2 },
      states: { hover: { filter: { type: 'darken', value: 0.9 } } },
      tooltip: { y: { formatter: function (v) { return v + ' of ' + total; } } },
      plotOptions: { pie: { donut: { size: '62%', labels: { show: true,
        value: { fontSize: '1.6rem', fontWeight: 700 },
        total: { show: true, showAlways: true, label: 'assigned', formatter: function () { return String(total); } } } } } }
    }).render();
    donut.style.cursor = 'pointer';
  }

  // Students: search by name.
  var filter = document.getElementById('studentFilter');
  if (filter) {
    filter.addEventListener('input', function () {
      var q = filter.value.trim().toLowerCase(), shown = 0;
      document.querySelectorAll('[data-student-row]').forEach(function (tr) {
        var on = !q || tr.dataset.name.indexOf(q) >= 0;
        tr.classList.toggle('d-none', !on);
        if (on) shown++;
      });
      document.getElementById('studentFilterEmpty').classList.toggle('d-none', shown > 0);
    });
  }

  // Questions: a question opens its answers below it; a link to #q-N opens that one.
  function toggle(btn, open) {
    var panel = document.getElementById(btn.getAttribute('aria-controls'));
    if (!panel) return;
    var on = open === undefined ? btn.getAttribute('aria-expanded') !== 'true' : open;
    btn.setAttribute('aria-expanded', on ? 'true' : 'false');
    panel.hidden = !on;
  }
  document.querySelectorAll('.question-head').forEach(function (btn) {
    btn.addEventListener('click', function () { toggle(btn); });
  });
  if (location.hash.indexOf('#q-') === 0) {
    var target = document.getElementById(location.hash.slice(1));
    var head = target && target.querySelector('.question-head');
    if (head) { toggle(head, true); target.scrollIntoView({ block: 'center' }); head.focus({ preventScroll: true }); }
  }

  // Formulas.
  function renderMath() {
    if (!window.renderMathInElement) return;
    page.querySelectorAll('.math-render').forEach(function (el) {
      renderMathInElement(el, { delimiters: [
        { left: '$$', right: '$$', display: true }, { left: '$', right: '$', display: false },
        { left: '\\(', right: '\\)', display: false }, { left: '\\[', right: '\\]', display: true }], throwOnError: false });
    });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', renderMath); else renderMath();

  // Score grid CSV.
  var exportBtn = document.getElementById('exportGrid');
  if (exportBtn) exportBtn.addEventListener('click', function () {
    var rows = [];
    document.querySelectorAll('#scoreGridTable tr').forEach(function (tr) {
      var cols = [];
      tr.querySelectorAll('th, td').forEach(function (c) { cols.push('"' + c.innerText.replace(/"/g, '""').trim() + '"'); });
      rows.push(cols.join(','));
    });
    var blob = new Blob(['﻿' + rows.join('\n')], { type: 'text/csv;charset=utf-8;' });
    var a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'class-scores.csv';
    a.click();
    URL.revokeObjectURL(a.href);
  });
})();
