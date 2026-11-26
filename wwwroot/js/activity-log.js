// Activity log for the assignment page: copy / cut / paste inside the assignment and opening the AI tutor, sent to the
// server in small batches for the teacher's Activity tab. Nothing typed or copied is ever read or sent — only which
// action, on which question, whether the page allowed it, and when. A lost connection keeps the batch (each action has
// its own id, so a resent batch is stored once). Logging never gets in the way of the work.
(function () {
  'use strict';

  var form = document.getElementById('assignmentForm');
  var assignmentEl = document.getElementById('assignmentId');
  var attemptEl = document.getElementById('attemptNumber');
  if (!form || !assignmentEl || !attemptEl) return;                 // preview mode, or not an assignment page

  var MAX_QUEUE = 200, MAX_BATCH = 50;
  var queue = [], sending = null, timer = null;

  function uuid() {
    if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
      var r = Math.random() * 16 | 0; return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
    });
  }

  function questionOf(node) {
    var panel = node && node.closest ? node.closest('.question-panel[data-qindex]') : null;
    return panel ? parseInt(panel.dataset.qindex, 10) : null;
  }

  function record(kind, question, outcome) {
    if (queue.length >= MAX_QUEUE) queue.shift();                    // bounded: the oldest goes first
    queue.push({ id: uuid(), kind: kind, outcome: outcome || null, question: question, at: Date.now() });
    schedule(queue.length >= 10 ? 0 : 3000);
  }

  function schedule(ms) {
    if (timer) clearTimeout(timer);
    timer = setTimeout(function () { timer = null; flush(); }, ms);
  }

  function token() {
    var t = document.querySelector('input[name="__RequestVerificationToken"]');
    return t ? t.value : '';
  }

  // Sends what is queued; on failure everything stays queued for the next try (same ids: stored once).
  function flush(keepalive) {
    if (sending) return sending;
    if (!queue.length) return Promise.resolve();
    var batch = queue.slice(0, MAX_BATCH);
    sending = fetch('/Student/LogActivity', {
      method: 'POST', credentials: 'same-origin', keepalive: !!keepalive,
      headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token(), 'X-CSRF-TOKEN': token() },
      body: JSON.stringify({ assignmentId: assignmentEl.value, attempt: parseInt(attemptEl.value, 10), sentAt: Date.now(), events: batch })
    }).then(function (res) {
      if (res.ok || res.status === 400) {                           // stored (or refused for good): drop these
        var sent = {}; batch.forEach(function (e) { sent[e.id] = true; });
        queue = queue.filter(function (e) { return !sent[e.id]; });
      }
    }).catch(function () { /* offline: keep them */ })
      .then(function () { sending = null; if (queue.length) schedule(5000); });
    return sending;
  }

  // Copy, cut and paste reach the page as one clipboard event each, whether from the keyboard, the context menu or a
  // phone's paste bubble — so a shortcut and its clipboard event are one action. Listened to in the capture phase so
  // a field that blocks pasting cannot hide the attempt.
  ['copy', 'cut', 'paste'].forEach(function (type) {
    document.addEventListener(type, function (e) {
      if (!form.contains(e.target)) return;
      var outcome = 'allowed';
      if (type === 'paste' && form.dataset.antiPaste === 'true' && e.target.closest && e.target.closest('.answer-textarea, .work-textarea'))
        outcome = 'blocked';                                          // the same rule the page applies (take-assignment.js)
      record(type, questionOf(e.target), outcome);
    }, true);
  });

  window.ActivityLog = {
    /** An explicit opening of the AI tutor (not a reload or a restored conversation). */
    tutorOpened: function (question) { record('tutor-open', question); },
    /** A new id for one tutor request, kept until its result. */
    newRequestId: uuid,
    /** Sends what is queued before the work is handed in (waits at most a moment). */
    flushNow: function (maxMs) {
      return Promise.race([flush(), new Promise(function (r) { setTimeout(r, maxMs || 1500); })]);
    }
  };

  window.addEventListener('pagehide', function () { flush(true); });
})();
