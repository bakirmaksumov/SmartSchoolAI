// The notifications bell. Its menu is loaded when it is opened (so pages carry no notification text until then);
// the new items are highlighted, and only after that is everything marked as seen. A page left open checks the
// unread count once a minute so new events show up on the badge.
(function () {
  'use strict';
  var root = document.getElementById('notifications');
  if (!root) return;
  var toggle = document.getElementById('notificationsToggle');
  var badge = document.getElementById('notificationsBadge');
  var menu = document.getElementById('notificationsMenu');
  var token = root.querySelector('input[name="__RequestVerificationToken"]');
  var loading = null;

  function show(n) {
    if (!badge) return;
    badge.hidden = n <= 0;
    badge.textContent = n > 9 ? '9+' : String(n);
    toggle.setAttribute('aria-label', n > 0 ? 'Notifications, ' + n + ' new' : 'Notifications');
  }

  function markSeen() {
    if (!badge || badge.hidden) return;
    fetch('/Notifications/Seen', {
      method: 'POST', credentials: 'same-origin',
      headers: token ? { 'X-CSRF-TOKEN': token.value } : {}
    }).then(function (r) { if (r.ok) show(0); }).catch(function () { /* stays unread; tried again next time */ });
  }

  toggle.addEventListener('show.bs.dropdown', function () {
    if (loading) return;
    loading = fetch('/Notifications/Menu', { credentials: 'same-origin', cache: 'no-store' })
      .then(function (r) { return r.ok ? r.text() : Promise.reject(r.status); })
      .then(function (html) {
        // Our own server-rendered partial (Razor-encoded); placed as the menu's content.
        menu.innerHTML = html;
        markSeen();
      })
      .catch(function () {
        menu.innerHTML = '<li class="text-center text-muted small py-4">Notifications could not be loaded. Try again.</li>';
      })
      .finally(function () { loading = null; });
  });

  setInterval(function () {
    if (document.hidden) return;
    fetch('/Notifications/Count', { credentials: 'same-origin', cache: 'no-store' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (d) { if (d && typeof d.unread === 'number') show(d.unread); })
      .catch(function () { });
  }, 60000);
})();
