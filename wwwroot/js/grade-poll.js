/*
 * Polls a background grading job until it finishes.
 *
 * The typed-answer check and the step evaluation both run server-side after the
 * request returns, so the browser is handed a jobId and asks
 * /Student/GradeProgress/{jobId} how it's going. This keeps one poll loop that all
 * three flows (Check answer, photo solution, typed solution) share.
 *
 *   var cancel = GradePoll.start(jobId, {
 *     onProgress: pct  => ...,   // called on each tick while still running (0–100)
 *     onDone:     data => ...,   // the job's result object
 *     onError:    msg  => ...    // a message to show the student
 *   });
 *   // cancel();  // stop polling early (e.g. the student closed the dialog)
 *
 * Each tick waits for the previous reply before scheduling the next, so a slow
 * response never stacks up overlapping requests.
 */
(function () {
  'use strict';

  var INTERVAL_MS = 1000;

  window.GradePoll = {
    start: function (jobId, handlers) {
      handlers = handlers || {};
      var cancelled = false;

      async function tick() {
        if (cancelled) return;
        try {
          var res  = await fetch('/Student/GradeProgress/' + jobId);
          var data = await res.json();
          if (cancelled) return;

          if (data.status === 'running') {
            if (handlers.onProgress) handlers.onProgress(data.percent || 0);
            setTimeout(tick, INTERVAL_MS);
            return;
          }

          if (data.status === 'error') {
            if (handlers.onError) handlers.onError(data.error || 'Something went wrong. Please try again.');
            return;
          }

          if (handlers.onProgress) handlers.onProgress(100);
          if (handlers.onDone) handlers.onDone(data.result);
        } catch (err) {
          if (cancelled) return;
          if (handlers.onError) handlers.onError('Network error. Please check your connection and try again.');
        }
      }

      tick();
      return function cancel() { cancelled = true; };
    }
  };
})();
