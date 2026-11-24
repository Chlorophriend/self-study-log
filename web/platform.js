/* Self-Study Log platform layer.
   Everything outside the app's own logic lives here, so the app runs on any host:
   - storage: this device only (browser storage), or synced across devices through your own Firebase project;
   - Google Calendar through Google's own sign-in and REST API;
   - saving exported files;
   - the Windows taskbar timer (TaskbarPill.exe), which listens on this computer only.
   The app talks to window.Platform and nothing else. */
(function () {
  'use strict';
  const CFG = window.SSL_CONFIG || {};
  const P = (window.Platform = {});

  // A Google sign-in window may only open right after a click or key press, so remember when the last one happened.
  let lastGesture = 0;
  const mark = () => { lastGesture = Date.now(); };
  document.addEventListener('pointerdown', mark, true);
  document.addEventListener('keydown', mark, true);
  const gesture = () => Date.now() - lastGesture < 1500;

  /* ---------- storage: collection(c).onSnapshot(fn) and collection(c).doc(id).set(data) / .delete() ---------- */

  // This device only: one JSON object in browser storage, shared live between open tabs.
  function localStore() {
    const KEY = 'ssl-local-v1';
    let data = {};
    try { data = JSON.parse(localStorage.getItem(KEY) || '{}') || {}; } catch (e) { data = {}; }
    const subs = {};
    const snap = c => ({ docs: Object.keys(data[c] || {}).map(id => ({ id: id, data: () => data[c][id] })) });
    const emit = c => (subs[c] || []).forEach(fn => { try { fn(snap(c)); } catch (e) { /* a broken listener must not stop the others */ } });
    const save = () => { try { localStorage.setItem(KEY, JSON.stringify(data)); } catch (e) { /* storage full or blocked: data stays in memory for this visit */ } };
    window.addEventListener('storage', e => {
      if (e.key !== KEY) return;
      try { data = JSON.parse(e.newValue || '{}') || {}; } catch (x) { return; }
      Object.keys(subs).forEach(emit);
    });
    return {
      kind: 'local',
      collection(c) {
        return {
          onSnapshot(next) { (subs[c] = subs[c] || []).push(next); setTimeout(() => next(snap(c)), 0); return () => { subs[c] = (subs[c] || []).filter(f => f !== next); }; },
          doc(id) {
            return {
              set(d) { (data[c] = data[c] || {})[id] = JSON.parse(JSON.stringify(d)); save(); emit(c); return Promise.resolve(); },
              delete() { if (data[c]) delete data[c][id]; save(); emit(c); return Promise.resolve(); }
            };
          }
        };
      }
    };
  }

  // Synced: Firestore in your own Firebase project, under users/<your id>/..., readable only by you (see firestore.rules).
  // The Firebase libraries load only when a Firebase project is configured.
  const FB = 'https://www.gstatic.com/firebasejs/10.12.2/';
  async function firebaseStore() {
    const [appM, authM, fsM] = await Promise.all([import(FB + 'firebase-app.js'), import(FB + 'firebase-auth.js'), import(FB + 'firebase-firestore.js')]);
    const app = appM.initializeApp(CFG.firebase);
    const auth = authM.getAuth(app);
    const user = await new Promise(res => { const un = authM.onAuthStateChanged(auth, u => { un(); res(u); }); });
    if (!user) {
      return { kind: 'signin', signIn: async () => { await authM.signInWithPopup(auth, new authM.GoogleAuthProvider()); location.reload(); } };
    }
    P.account = { mode: 'firebase', email: user.email || '', signOut: async () => { await authM.signOut(auth); location.reload(); } };
    let db;
    try { db = fsM.initializeFirestore(app, { localCache: fsM.persistentLocalCache({ tabManager: fsM.persistentMultipleTabManager() }) }); }
    catch (e) { db = fsM.getFirestore(app); }
    const base = ['users', user.uid];
    return {
      kind: 'firebase',
      collection(c) {
        const ref = fsM.collection(db, ...base, c);
        return {
          onSnapshot(next, err) { return fsM.onSnapshot(ref, next, err); },
          doc(id) { const d = fsM.doc(db, ...base, c, id); return { set: v => fsM.setDoc(d, v), delete: () => fsM.deleteDoc(d) }; }
        };
      }
    };
  }

  P.account = { mode: 'local', email: '' };
  let store = null;
  P.openStore = async () => {
    const s = await (CFG.firebase && CFG.firebase.apiKey ? firebaseStore() : localStore());
    if (s.kind !== 'signin') { store = s; watchBridge(s); }
    return s;
  };

  /* ---------- calendar bridge: a small Apps Script in your own Google account ---------- */
  // Google gives a browser-only app one hour of calendar access at a time. An Apps Script web app runs as you and keeps
  // its permission until you remove it, so the log reaches your calendar through it for months without reconnecting.
  // Its address and secret key live in your private data (users/<you>/private/calendarBridge), never in the published files.
  let bridge = null;
  function watchBridge(s) {
    s.collection('private').onSnapshot(snap => {
      const d = snap.docs.find(x => x.id === 'calendarBridge');
      bridge = d ? d.data() : null;
      if (P.onBridge) P.onBridge();
    }, () => {});
  }
  const bridgeOn = () => !!(bridge && bridge.url && bridge.key);
  P.bridgeInfo = () => (bridge ? { url: bridge.url || '', hasKey: !!bridge.key, on: bridgeOn() } : null);
  P.bridgeUrlOk = u => /^https:\/\/script\.google\.com\/(a\/[^/]+\/)?macros\/s\/[\w-]+\/exec$/.test(u);
  function newKey() {
    const b = new Uint8Array(24); crypto.getRandomValues(b);
    return btoa(String.fromCharCode.apply(null, b)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }
  // The script text to paste into script.google.com, with this log's key in it (a key is made the first time).
  P.bridgeScript = async () => {
    if (!store) throw new Error('log not open');
    let key = bridge && bridge.key;
    if (!key) { key = newKey(); bridge = { url: '', key: key }; await store.collection('private').doc('calendarBridge').set(bridge); }
    return BRIDGE_SCRIPT.replace('__KEY__', key);
  };
  P.bridgeSave = async url => { if (!store || !bridge || !bridge.key) throw new Error('copy the script first'); bridge = { url: url, key: bridge.key }; await store.collection('private').doc('calendarBridge').set(bridge); };
  P.bridgeRemove = async () => { if (store) await store.collection('private').doc('calendarBridge').delete(); bridge = null; };
  async function viaBridge(tool, input) {
    let r, j;
    try {
      // text/plain keeps this a "simple" request: Apps Script cannot answer the browser's pre-flight check.
      r = await fetch(bridge.url, { method: 'POST', headers: { 'Content-Type': 'text/plain;charset=utf-8' }, body: JSON.stringify({ key: bridge.key, tool: tool, input: input }), redirect: 'follow' });
    } catch (e) { throw { code: 'bridge_unreachable', retryable: true }; }
    try { j = await r.json(); } catch (e) { throw { code: 'bridge_setup' }; } // a sign-in page instead of JSON: not deployed for "Anyone"
    if (j && !j.ok && j.message === 'wrong key') throw { code: 'bridge_key' };
    if (!j || !j.ok) throw { code: (j && j.code) || 'tool_error', retryable: !!(j && j.retryable), message: j && j.message };
    return j.data;
  }
  P.bridgeTest = () => viaBridge('list_calendars', {});

  const BRIDGE_SCRIPT = [
    '// Self-Study Log calendar bridge. It runs in your own Google account and answers only requests that carry KEY.',
    '// Setup: Services (+) > Google Calendar API > Add. Then Deploy > New deployment > Web app,',
    '// Execute as: Me, Who has access: Anyone. Paste the Web app URL into the log (Setup > Google Calendar connection).',
    "const KEY = '__KEY__';",
    '',
    'function doPost(e) {',
    '  let out;',
    '  try {',
    '    const req = JSON.parse(e.postData.contents);',
    "    if (req.key !== KEY) out = { ok: false, code: 'not_granted', message: 'wrong key' };",
    '    else out = { ok: true, data: run(req.tool, req.input || {}) };',
    '  } catch (err) {',
    '    const m = String((err && err.message) || err);',
    '    const busy = /rate limit|quota|backend error|service invoked too many times|try again/i.test(m);',
    "    out = { ok: false, code: busy ? 'server_unavailable' : 'tool_error', retryable: busy, message: m };",
    '  }',
    '  return ContentService.createTextOutput(JSON.stringify(out)).setMimeType(ContentService.MimeType.JSON);',
    '}',
    '',
    'function doGet() {',
    "  return ContentService.createTextOutput('The Self-Study Log calendar bridge is running.');",
    '}',
    '',
    'function run(tool, i) {',
    "  const cal = i.calendarId || 'primary';",
    "  if (tool === 'list_calendars') {",
    '    const r = Calendar.CalendarList.list({ maxResults: 250 });',
    '    return { calendars: (r.items || []).map(c => ({ id: c.id, summary: c.summaryOverride || c.summary })) };',
    '  }',
    "  if (tool === 'list_events') {",
    "    const q = { timeMin: i.timeMin, timeMax: i.timeMax, singleEvents: true, orderBy: 'startTime', maxResults: i.maxResults || 250 };",
    '    if (i.pageToken) q.pageToken = i.pageToken;',
    '    const r = Calendar.Events.list(cal, q);',
    '    return { events: r.items || [], nextPageToken: r.nextPageToken };',
    '  }',
    "  if (tool === 'create_event') return Calendar.Events.insert(i.body, cal);",
    "  if (tool === 'update_event') return Calendar.Events.patch(i.body, cal, i.eventId);",
    "  throw new Error('unknown call ' + tool);",
    '}',
    ''
  ].join('\n');

  /* ---------- Google Calendar ---------- */
  // Google Identity Services issues a one-hour access token. A new one needs a click (Google opens and closes a
  // small window when you have already agreed), so background reads use the token while it lasts and wait otherwise.
  const SCOPES = 'https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.calendarlist.readonly';
  let tok = null, tokExp = 0, tokenClient = null, gis = null, tokPending = null;
  try { const t = JSON.parse(localStorage.getItem('ssl-gtok') || 'null'); if (t && t.e > Date.now()) { tok = t.t; tokExp = t.e; } } catch (e) { /* no saved token */ }
  function loadGis() {
    if (!gis) gis = new Promise((res, rej) => {
      const s = document.createElement('script');
      s.src = 'https://accounts.google.com/gsi/client'; s.async = true;
      s.onload = () => res(); s.onerror = () => { gis = null; rej({ code: 'server_unavailable', retryable: true }); };
      document.head.appendChild(s);
    });
    return gis;
  }
  P.calendarConfigured = () => !!CFG.googleClientId || bridgeOn();
  if (CFG.googleClientId) loadGis().catch(() => {});

  // Backup when no bridge is set up: once the hour has run out, the next click anywhere in the app renews access
  // (Google opens and closes a small window), then the app re-reads the calendar through P.onCalendarRenewed.
  let renewing = false;
  document.addEventListener('click', () => {
    if (renewing || bridgeOn() || !CFG.googleClientId || (tok && Date.now() < tokExp - 60000)) return;
    if (!(P.calendarWanted && P.calendarWanted())) return;
    let agreed = false; try { agreed = !!localStorage.getItem('ssl-gconsent'); } catch (e) { /* not yet agreed */ }
    if (!agreed) return;
    renewing = true;
    token().then(() => { if (P.onCalendarRenewed) P.onCalendarRenewed(); }, () => {}).then(() => { renewing = false; });
  });

  function token() {
    if (tok && Date.now() < tokExp - 60000) return Promise.resolve(tok);
    if (!CFG.googleClientId) return Promise.reject({ code: 'server_not_connected' });
    if (!window.google || !google.accounts || !google.accounts.oauth2) { loadGis().catch(() => {}); return Promise.reject({ code: 'server_unavailable', retryable: true }); }
    if (tokPending) return tokPending; // one Google window at a time; everyone waiting shares its answer
    if (!gesture()) return Promise.reject({ code: 'approval_required' });
    tokPending = new Promise((res, rej) => {
      tokenClient = tokenClient || google.accounts.oauth2.initTokenClient({ client_id: CFG.googleClientId, scope: SCOPES, callback: () => {} });
      tokenClient.callback = r => {
        if (r.error) return rej({ code: r.error === 'access_denied' ? 'not_granted' : 'approval_required', message: r.error });
        tok = r.access_token; tokExp = Date.now() + (Number(r.expires_in) || 3600) * 1000;
        try { localStorage.setItem('ssl-gtok', JSON.stringify({ t: tok, e: tokExp })); localStorage.setItem('ssl-gconsent', '1'); } catch (e) { /* not kept between visits */ }
        res(tok);
      };
      tokenClient.error_callback = e => rej({ code: 'approval_required', message: (e && e.type) || 'window closed' });
      let agreed = false; try { agreed = !!localStorage.getItem('ssl-gconsent'); } catch (e) { /* ask again */ }
      tokenClient.requestAccessToken({ prompt: agreed ? '' : 'consent' });
    });
    tokPending.then(() => { tokPending = null; }, () => { tokPending = null; });
    return tokPending;
  }

  const API = 'https://www.googleapis.com/calendar/v3';
  const rfc = s => new Date(s).toISOString(); // the app passes local wall-clock times; Google wants an instant
  async function api(method, path, body, query) {
    const t = await token();
    const url = API + path + (query ? '?' + new URLSearchParams(query) : '');
    let r;
    try {
      r = await fetch(url, { method: method, headers: Object.assign({ Authorization: 'Bearer ' + t }, body ? { 'Content-Type': 'application/json' } : {}), body: body ? JSON.stringify(body) : undefined });
    } catch (e) { throw { code: 'server_unavailable', retryable: true }; }
    if (r.status === 401) { tok = null; throw { code: 'approval_required' }; }
    if (r.status === 429 || r.status >= 500) throw { code: 'server_unavailable', retryable: true };
    if (r.status === 403) { const j = await r.json().catch(() => ({})); const why = JSON.stringify(j); throw { code: /rateLimit/i.test(why) ? 'server_unavailable' : 'not_granted', retryable: /rateLimit/i.test(why) }; }
    if (!r.ok) throw { code: 'tool_error', message: 'Google Calendar answered ' + r.status };
    return r.status === 204 ? {} : r.json();
  }

  // The app asks for list_calendars, list_events, create_event and update_event. They go through your calendar bridge
  // when one is set up, otherwise straight to the Calendar API with the one-hour access.
  P.calendar = async (tool, input) => {
    input = input || {};
    const calId = input.calendarId || 'primary', cal = encodeURIComponent(calId);
    const viaB = bridgeOn();
    if (tool === 'list_calendars') {
      if (viaB) return viaBridge('list_calendars', {});
      const q = await api('GET', '/users/me/calendarList', null, { maxResults: '250' });
      return { calendars: (q.items || []).map(c => ({ id: c.id, summary: c.summaryOverride || c.summary })) };
    }
    if (tool === 'list_events') {
      if (viaB) return viaBridge('list_events', { calendarId: calId, timeMin: rfc(input.startTime), timeMax: rfc(input.endTime), maxResults: input.pageSize || 250, pageToken: input.pageToken || '' });
      const q = { timeMin: rfc(input.startTime), timeMax: rfc(input.endTime), singleEvents: 'true', orderBy: 'startTime', maxResults: String(input.pageSize || 250) };
      if (input.pageToken) q.pageToken = input.pageToken;
      const r = await api('GET', '/calendars/' + cal + '/events', null, q);
      return { events: r.items || [], nextPageToken: r.nextPageToken };
    }
    if (tool === 'create_event' || tool === 'update_event') {
      const b = {};
      if (input.summary != null) b.summary = input.summary;
      if (input.description != null) b.description = input.description;
      if (input.startTime) b.start = { dateTime: rfc(input.startTime) };
      if (input.endTime) b.end = { dateTime: rfc(input.endTime) };
      if (input.availability) b.transparency = input.availability === 'AVAILABILITY_FREE' ? 'transparent' : 'opaque';
      if (input.overrideReminders) b.reminders = { useDefault: false, overrides: input.overrideReminders };
      else if (input.useDefaultReminders === false) b.reminders = { useDefault: false, overrides: [] };
      if (tool === 'create_event') {
        b.reminders = b.reminders || { useDefault: false, overrides: [] };
        return viaB ? viaBridge('create_event', { calendarId: calId, body: b }) : api('POST', '/calendars/' + cal + '/events', b);
      }
      return viaB ? viaBridge('update_event', { calendarId: calId, eventId: input.eventId, body: b }) : api('PATCH', '/calendars/' + cal + '/events/' + encodeURIComponent(input.eventId), b);
    }
    throw { code: 'bad_request', message: 'unknown calendar call ' + tool };
  };

  /* ---------- files ---------- */
  P.saveFile = (name, data) => {
    const type = /\.json$/i.test(name) ? 'application/json' : /\.csv$/i.test(name) ? 'text/csv' : 'text/plain';
    const url = URL.createObjectURL(new Blob([data], { type: type }));
    const a = document.createElement('a'); a.href = url; a.download = name;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 3000);
  };

  /* ---------- Windows taskbar timer ---------- */
  // TaskbarPill.exe serves this app at http://localhost:47613 and draws the clock on the taskbar.
  // When the app is open from there it talks to the pill directly; from another address it reaches it on this computer only.
  const local = /^(localhost|127\.0\.0\.1):47613$/.test(location.host);
  const PILL = local ? '/api' : 'http://localhost:47613/api';
  let pillOn = false, pillLast = '', pillSent = 0;
  P.pillProbe = async () => {
    if (!local && !/Windows/i.test(navigator.userAgent)) return false;
    try { const r = await fetch(PILL + '/ping', { cache: 'no-store' }); pillOn = r.ok; } catch (e) { pillOn = false; }
    return pillOn;
  };
  P.pillConnected = () => pillOn;
  // Sends the session state when it changes, and at least every 30 seconds so a restarted pill catches up.
  P.pill = state => {
    if (!pillOn) return;
    const s = JSON.stringify(state);
    if (s === pillLast && Date.now() - pillSent < 30000) return;
    pillLast = s; pillSent = Date.now();
    fetch(PILL + '/state', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: s }).catch(() => { pillOn = false; });
  };
})();
