// The assignment page: tabs, counter filters, search, anonymous mode, formulas and the live board.
// The live board never reloads the page: when the server's signature changes it fetches just the counters and
// student rows (?fragment=live) and swaps the rows that changed. The open tab, filter, search text, focus,
// scroll position and expanded attempts all stay as they were.
(function () {
  'use strict';

  var page = document.getElementById('assignmentPage');
  if (!page) return;

  var assignmentId = page.dataset.assignment;
  // Read before the address is tidied up below.
  var reopenAssign = new URLSearchParams(location.search).get('assign') === 'open';
  var state = {
    tab: new URLSearchParams(location.search).get('tab') || 'students',
    filter: new URLSearchParams(location.search).get('filter') || 'all',
    search: ''
  };
  var FILTER_LABELS = {
    'all': '',
    'submitted': 'Showing students who submitted.',
    'not-submitted': 'Showing students who have not submitted (in progress or not started).',
    'needs-review': 'Showing students with work that needs your review.'
  };

  // ── Tabs ─────────────────────────────────────────────────────────────────
  function showTab(tab) {
    state.tab = ['students', 'questions', 'details'].indexOf(tab) >= 0 ? tab : 'students';
    document.querySelectorAll('#detailTabs [data-tab]').forEach(function (a) {
      var on = a.dataset.tab === state.tab;
      a.classList.toggle('active', on);
      a.setAttribute('aria-selected', on ? 'true' : 'false');
    });
    document.querySelectorAll('[data-pane]').forEach(function (p) { p.classList.toggle('d-none', p.dataset.pane !== state.tab); });
    writeUrl();
  }

  function writeUrl() {
    var params = new URLSearchParams(location.search);
    params.set('tab', state.tab);
    if (state.filter && state.filter !== 'all') params.set('filter', state.filter); else params.delete('filter');
    params.delete('assign');
    history.replaceState(null, '', location.pathname + '?' + params.toString());
  }

  document.getElementById('detailTabs').addEventListener('click', function (e) {
    var link = e.target.closest('[data-tab]');
    if (!link) return;
    e.preventDefault();
    showTab(link.dataset.tab);
  });

  // ── Filters (counter cards) and search ───────────────────────────────────
  function rowMatches(tbody) {
    var f = state.filter;
    var okFilter = f === 'all'
      || (f === 'submitted' && tbody.dataset.state === 'submitted')
      || (f === 'not-submitted' && tbody.dataset.state !== 'submitted')
      || (f === 'needs-review' && tbody.dataset.review === 'needs');
    var okSearch = !state.search || (tbody.dataset.search || '').indexOf(state.search) >= 0;
    return okFilter && okSearch;
  }

  function applyRows() {
    var rows = document.querySelectorAll('#recipientTable tbody.recipient');
    var shown = 0;
    rows.forEach(function (t) {
      var on = rowMatches(t);
      t.classList.toggle('d-none', !on);
      if (on) shown++;
    });
    var count = document.getElementById('showingCount');
    if (count) count.textContent = rows.length ? 'Showing ' + shown + ' of ' + rows.length + '.' : '';
    var label = document.getElementById('filterLabel');
    if (label) label.textContent = FILTER_LABELS[state.filter] || '';
    var clear = document.getElementById('clearFilter');
    if (clear) clear.classList.toggle('d-none', state.filter === 'all');
    document.querySelectorAll('#liveCounters [data-filter]').forEach(function (c) {
      var on = c.dataset.filter === state.filter;
      c.classList.toggle('active', on);
      c.setAttribute('aria-pressed', on ? 'true' : 'false');
    });
  }

  function setFilter(f) {
    state.filter = FILTER_LABELS.hasOwnProperty(f) ? f : 'all';
    applyRows();
    showTab('students');
  }

  document.getElementById('liveCounters').addEventListener('click', function (e) {
    var card = e.target.closest('[data-filter]');
    if (!card) return;
    e.preventDefault();
    setFilter(card.dataset.filter);
  });
  var clearBtn = document.getElementById('clearFilter');
  if (clearBtn) clearBtn.addEventListener('click', function () { setFilter('all'); });

  var search = document.getElementById('studentSearch');
  if (search) search.addEventListener('input', function () {
    state.search = search.value.trim().toLowerCase();
    applyRows();
  });

  // ── Anonymous mode (kept for this tab of the browser) ────────────────────
  var anonKey = 'assignmentAnon:' + assignmentId;
  var anon = document.getElementById('anonToggle');
  function setAnon(on) {
    document.body.classList.toggle('anon-on', on);
    try { sessionStorage.setItem(anonKey, on ? '1' : ''); } catch (e) { /* best effort */ }
  }
  if (anon) {
    var stored = false;
    try { stored = sessionStorage.getItem(anonKey) === '1'; } catch (e) { stored = false; }
    anon.checked = stored;
    setAnon(stored);
    anon.addEventListener('change', function () { setAnon(anon.checked); });
  }

  // ── Formulas ─────────────────────────────────────────────────────────────
  function renderMath(root) {
    if (!window.renderMathInElement) return;
    (root || page).querySelectorAll('.math-render').forEach(function (el) {
      renderMathInElement(el, {
        delimiters: [
          { left: '$$', right: '$$', display: true },
          { left: '$', right: '$', display: false },
          { left: '\\(', right: '\\)', display: false },
          { left: '\\[', right: '\\]', display: true }
        ],
        throwOnError: false
      });
    });
  }

  // ── Live board ───────────────────────────────────────────────────────────
  var announcer = document.createElement('div');
  announcer.className = 'visually-hidden';
  announcer.setAttribute('aria-live', 'polite');
  announcer.id = 'liveAnnouncer';
  page.appendChild(announcer);

  function refocus(container, focusId) {
    if (!focusId) return;
    var el = container.querySelector('[data-focus-id="' + focusId + '"]');
    if (el) el.focus({ preventScroll: true });
  }

  function patch(html) {
    var doc = new DOMParser().parseFromString(html, 'text/html');
    var frag = doc.getElementById('liveFragment');
    if (!frag) return;

    var active = document.activeElement;
    var focusId = active && active.dataset ? active.dataset.focusId : null;
    var focusFilter = active && active.closest && active.closest('#liveCounters [data-filter]');

    // Counters: new numbers, the same filter highlighted; a changed number gets a tiny bump.
    var counters = document.getElementById('liveCounters');
    var oldValues = {};
    counters.querySelectorAll('[data-counter]').forEach(function (c) { oldValues[c.dataset.counter] = c.querySelector('.counter-value').textContent; });
    counters.innerHTML = frag.querySelector('#liveCounters').innerHTML;
    counters.querySelectorAll('[data-counter]').forEach(function (c) {
      var v = c.querySelector('.counter-value');
      if (oldValues[c.dataset.counter] !== undefined && oldValues[c.dataset.counter] !== v.textContent) v.classList.add('bumped');
    });
    if (focusFilter) {
      var again = counters.querySelector('[data-filter="' + focusFilter.dataset.filter + '"]');
      if (again) again.focus({ preventScroll: true });
    }

    // Rows: replace only students whose row changed; keep their attempts open and keep focus inside them.
    var table = document.getElementById('recipientTable');
    var existing = {};
    table.querySelectorAll('tbody.recipient').forEach(function (t) { existing[t.dataset.student] = t; });
    var changed = 0;
    var incoming = frag.querySelectorAll('tbody.recipient');
    incoming.forEach(function (fresh) {
      var id = fresh.dataset.student;
      var old = existing[id];
      var node = document.importNode(fresh, true);
      if (old && old.dataset.version === fresh.dataset.version) {
        table.appendChild(old);                   // unchanged: the very same element, just kept in order
        delete existing[id];
        return;
      }
      if (old) {
        var wasOpen = old.querySelector('.collapse.show');
        if (wasOpen) {
          var panel = node.querySelector('.collapse');
          if (panel) panel.classList.add('show');
          var toggle = node.querySelector('.attempt-toggle');
          if (toggle) toggle.setAttribute('aria-expanded', 'true');
        }
        var hadFocus = old.contains(active);
        old.replaceWith(node);
        table.appendChild(node);
        if (hadFocus) refocus(node, focusId);
        delete existing[id];
      } else {
        table.appendChild(node);
      }
      node.classList.add('row-updated');
      setTimeout(function () { node.classList.remove('row-updated'); }, 1800);
      changed++;
    });
    Object.keys(existing).forEach(function (id) { existing[id].remove(); });  // no longer a recipient

    var former = document.getElementById('liveFormer');
    var freshFormer = frag.querySelector('#liveFormer');
    if (former && freshFormer && former.innerHTML.trim() !== freshFormer.innerHTML.trim()) former.innerHTML = freshFormer.innerHTML;

    page.dataset.signature = frag.dataset.signature;
    applyRows();
    if (changed) announcer.textContent = changed === 1 ? 'One student row was updated.' : changed + ' student rows were updated.';
  }

  var polling = false;
  async function poll() {
    if (polling || document.visibilityState !== 'visible') return;
    polling = true;
    try {
      var res = await fetch('/Assignments/LiveStatus/' + assignmentId, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' });
      if (!res.ok) return;
      var data = await res.json();
      if (data.signature && data.signature !== page.dataset.signature) {
        var frag = await fetch('/Assignments/Details/' + assignmentId + '?fragment=live', { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' });
        if (frag.ok) patch(await frag.text());
      }
    } catch (e) {
      /* a passing network error — try again next time */
    } finally {
      polling = false;
    }
  }

  if (page.dataset.live === 'true') {
    var interval = parseInt(page.dataset.pollMs || '10000', 10);
    setInterval(poll, interval);
    document.addEventListener('visibilitychange', function () { if (document.visibilityState === 'visible') poll(); });
    window.assignmentLive = { poll: poll };     // lets a test (or a teacher's "refresh") ask right away
  }

  // ── Start ────────────────────────────────────────────────────────────────
  showTab(state.tab);
  applyRows();
  document.addEventListener('DOMContentLoaded', function () { renderMath(); });
  if (document.readyState !== 'loading') renderMath();

  // Back from a failed assign attempt or from inviting students: the assign panel opens again.
  document.addEventListener('DOMContentLoaded', function () {
    var modal = document.getElementById('assignModal');
    if (modal && (reopenAssign || document.getElementById('assignError'))) {
      bootstrap.Modal.getOrCreateInstance(modal).show();
    }
  });
})();
