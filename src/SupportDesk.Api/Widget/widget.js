/*!
 * SupportDesk live-chat widget (vanilla JS, no dependencies).
 * Embed:  <script src="https://your-desk/widget/widget.js" data-widget-key="wk_…" async></script>
 * Optional attributes: data-name, data-email (prefill), data-open="true" (open on load), data-position="left".
 * Renders inside a shadow root (no style leakage), uses the brand colours from /api/v1/portal/{brand}/config and talks to
 * /api/v1/chat/{widgetKey}/… with the signed visitor token; live updates arrive over SignalR (/hubs/chat, JSON protocol
 * over WebSocket) with a polling fallback.
 */
(function () {
  'use strict';
  var script = document.currentScript || (function () {
    var all = document.querySelectorAll('script[data-widget-key]');
    return all[all.length - 1];
  })();
  if (!script || window.__supportDeskChatLoaded) return;
  window.__supportDeskChatLoaded = true;

  var widgetKey = script.getAttribute('data-widget-key');
  // The demo page lets admins preview any widget: /widget/demo.html?widgetKey=wk_… (only with data-allow-key-override).
  if (script.hasAttribute('data-allow-key-override')) {
    try {
      var override = new URLSearchParams(location.search).get('widgetKey');
      if (override && /^[A-Za-z0-9_-]{3,120}$/.test(override)) widgetKey = override;
    } catch (e) { /* ignore */ }
  }
  if (!widgetKey) { console.warn('SupportDesk chat: data-widget-key is missing.'); return; }
  // Origin plus the app's path base (e.g. https://help.example.com/supportdesk when hosted in an IIS virtual directory).
  var scriptUrl = new URL(script.src, location.href);
  var base = scriptUrl.origin + scriptUrl.pathname.replace(/\/widget\/widget\.js$/i, '');
  var api = base + '/api/v1/chat/' + encodeURIComponent(widgetKey);
  var storeKey = 'sdchat:' + widgetKey;
  var RS = '\u001e';

  var state = {
    config: null, conversationId: null, token: null, status: null, messages: [], seen: {}, lastAt: null,
    socket: null, pollTimer: null, pingTimer: null, open: false, typingTimer: null, verified: false, agentName: null
  };

  // ───────────── storage (best effort) ─────────────
  function load() { try { return JSON.parse(localStorage.getItem(storeKey) || 'null'); } catch (e) { return null; } }
  function save() { try { localStorage.setItem(storeKey, JSON.stringify({ conversationId: state.conversationId, token: state.token })); } catch (e) { /* ignore */ } }
  function forget() { try { localStorage.removeItem(storeKey); } catch (e) { /* ignore */ } }

  // ───────────── http ─────────────
  function request(method, path, body) {
    var headers = { 'Accept': 'application/json' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (state.token) headers['X-Visitor-Token'] = state.token;
    return fetch(api + path, { method: method, headers: headers, body: body === undefined ? undefined : JSON.stringify(body), mode: 'cors', credentials: 'omit' })
      .then(function (r) {
        if (r.status === 204) return null;
        return r.json().catch(function () { return null; }).then(function (data) {
          if (!r.ok) { var err = new Error((data && data.title) || ('Request failed (' + r.status + ')')); err.status = r.status; throw err; }
          return data;
        });
      });
  }

  // ───────────── DOM ─────────────
  var host = document.createElement('div');
  host.setAttribute('data-supportdesk-chat', '');
  var root = host.attachShadow({ mode: 'open' });
  var left = script.getAttribute('data-position') === 'left';
  var css = [
    ':host{all:initial}',
    '*{box-sizing:border-box;font-family:system-ui,-apple-system,"Segoe UI",Roboto,Arial,sans-serif}',
    '.launcher{position:fixed;bottom:20px;' + (left ? 'left' : 'right') + ':20px;width:56px;height:56px;border-radius:50%;border:0;cursor:pointer;color:#fff;background:var(--sd-primary);box-shadow:0 6px 20px rgba(0,0,0,.25);z-index:2147483646;display:flex;align-items:center;justify-content:center}',
    '.launcher:focus-visible,button:focus-visible,textarea:focus-visible,input:focus-visible{outline:3px solid var(--sd-accent);outline-offset:2px}',
    '.badge{position:absolute;top:-2px;right:-2px;background:#dc2626;color:#fff;border-radius:10px;font-size:11px;padding:1px 6px;display:none}',
    '.panel{position:fixed;bottom:88px;' + (left ? 'left' : 'right') + ':20px;width:min(380px,calc(100vw - 32px));height:min(560px,calc(100vh - 120px));background:#fff;color:#111827;border-radius:14px;box-shadow:0 12px 40px rgba(0,0,0,.28);display:none;flex-direction:column;overflow:hidden;z-index:2147483647}',
    '.panel.open{display:flex}',
    'header{background:var(--sd-primary);color:#fff;padding:12px 14px;display:flex;align-items:center;gap:8px}',
    'header .t{flex:1;min-width:0}header h2{margin:0;font-size:15px;font-weight:600}header p{margin:2px 0 0;font-size:12px;opacity:.9}',
    'header button{background:transparent;border:0;color:#fff;cursor:pointer;font-size:18px;padding:4px 6px;border-radius:6px}',
    '.log{flex:1;overflow-y:auto;padding:12px;background:#f8fafc;display:flex;flex-direction:column;gap:8px}',
    '.msg{max-width:82%;padding:8px 11px;border-radius:12px;font-size:14px;line-height:1.4;white-space:pre-wrap;word-wrap:break-word}',
    '.msg.visitor{align-self:flex-end;background:var(--sd-primary);color:#fff;border-bottom-right-radius:4px}',
    '.msg.agent,.msg.bot{align-self:flex-start;background:#fff;border:1px solid #e5e7eb;border-bottom-left-radius:4px}',
    '.msg.system{align-self:center;background:transparent;color:#6b7280;font-size:12px;text-align:center}',
    '.who{display:block;font-size:11px;color:#6b7280;margin-bottom:2px}.msg.visitor .who{color:rgba(255,255,255,.8)}',
    '.cites{margin-top:6px;font-size:12px}.cites a{color:var(--sd-primary)}',
    '.typing{font-size:12px;color:#6b7280;padding:0 12px 6px;min-height:18px;background:#f8fafc}',
    'form.compose{display:flex;gap:8px;padding:10px;border-top:1px solid #e5e7eb;background:#fff}',
    'textarea{flex:1;resize:none;border:1px solid #d1d5db;border-radius:10px;padding:8px 10px;font-size:14px;height:42px;max-height:120px;color:#111827}',
    'button.primary{background:var(--sd-primary);color:#fff;border:0;border-radius:10px;padding:0 14px;font-size:14px;cursor:pointer;min-height:42px}',
    'button.primary:disabled{opacity:.5;cursor:default}',
    '.actions{display:flex;gap:6px;flex-wrap:wrap;padding:6px 10px 0;background:#fff}',
    '.actions button{background:#f1f5f9;border:1px solid #e2e8f0;color:#0f172a;border-radius:999px;font-size:12px;padding:5px 10px;cursor:pointer}',
    '.prechat{padding:16px;display:flex;flex-direction:column;gap:10px;overflow-y:auto}',
    '.prechat label{font-size:13px;color:#374151;display:flex;flex-direction:column;gap:4px}',
    '.prechat input,.prechat textarea{border:1px solid #d1d5db;border-radius:8px;padding:8px 10px;font-size:14px;height:auto;color:#111827}',
    '.err{color:#b91c1c;font-size:12px;min-height:16px}',
    '.rating{display:flex;gap:6px;justify-content:center;padding:6px}.rating button{border:1px solid #e5e7eb;background:#fff;border-radius:8px;padding:6px 9px;cursor:pointer;font-size:14px}',
    '@media (prefers-reduced-motion:no-preference){.panel.open{animation:sdIn .18s ease-out}@keyframes sdIn{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:none}}}'
  ].join('\n');
  try {
    var sheet = new CSSStyleSheet(); sheet.replaceSync(css); root.adoptedStyleSheets = [sheet];
  } catch (e) {
    var style = document.createElement('style'); style.textContent = css; root.appendChild(style);
  }

  function el(tag, attrs, children) {
    var node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (k) {
      if (k === 'text') node.textContent = attrs[k];
      else if (k.indexOf('on') === 0) node.addEventListener(k.slice(2), attrs[k]);
      else node.setAttribute(k, attrs[k]);
    });
    (children || []).forEach(function (c) { if (c) node.appendChild(c); });
    return node;
  }

  var launcher = el('button', { class: 'launcher', type: 'button', 'aria-label': 'Open chat', 'aria-expanded': 'false', onclick: toggle });
  launcher.innerHTML = '<svg width="26" height="26" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M4 5.5A2.5 2.5 0 0 1 6.5 3h11A2.5 2.5 0 0 1 20 5.5v8A2.5 2.5 0 0 1 17.5 16H9l-4.2 3.6A.5.5 0 0 1 4 19.2V5.5Z" fill="currentColor"/></svg>';
  var badge = el('span', { class: 'badge', 'aria-hidden': 'true' });
  launcher.appendChild(badge);
  var title = el('h2', { text: 'Chat with us' });
  var subtitle = el('p', { text: 'We typically reply in a few minutes' });
  var closeBtn = el('button', { type: 'button', 'aria-label': 'Minimise chat', text: '–', onclick: toggle });
  var endBtn = el('button', { type: 'button', 'aria-label': 'End chat', title: 'End chat', text: '✕', onclick: endChat });
  var header = el('header', {}, [el('div', { class: 't' }, [title, subtitle]), endBtn, closeBtn]);
  var log = el('div', { class: 'log', role: 'log', 'aria-live': 'polite', 'aria-label': 'Chat messages' });
  var typing = el('div', { class: 'typing', 'aria-live': 'polite' });
  var input = el('textarea', { rows: '1', placeholder: 'Type your message…', 'aria-label': 'Message', maxlength: '4000' });
  var sendBtn = el('button', { class: 'primary', type: 'submit', text: 'Send' });
  var compose = el('form', { class: 'compose', onsubmit: onSend }, [input, sendBtn]);
  var humanBtn = el('button', { type: 'button', text: 'Talk to a person', onclick: handoff });
  var verifyBtn = el('button', { type: 'button', text: 'Verify my e-mail', onclick: verify });
  var actions = el('div', { class: 'actions' }, [humanBtn, verifyBtn]);
  var prechat = buildPrechat();
  var panel = el('section', { class: 'panel', role: 'dialog', 'aria-label': 'Live chat' }, [header, prechat, log, typing, actions, compose]);
  root.appendChild(launcher);
  root.appendChild(panel);

  input.addEventListener('keydown', function (e) {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); onSend(e); }
  });
  input.addEventListener('input', function () { signalTyping(true); });
  panel.addEventListener('keydown', function (e) { if (e.key === 'Escape') toggle(); });

  function buildPrechat() {
    var name = el('input', { type: 'text', autocomplete: 'name', value: script.getAttribute('data-name') || '' });
    var email = el('input', { type: 'email', autocomplete: 'email', value: script.getAttribute('data-email') || '' });
    var first = el('textarea', { rows: '3', placeholder: 'How can we help?' });
    var err = el('div', { class: 'err', role: 'alert' });
    var start = el('button', { class: 'primary', type: 'submit', text: 'Start chat' });
    var form = el('form', { class: 'prechat', onsubmit: function (e) {
      e.preventDefault();
      if (state.config && state.config.requireEmail && !email.value.trim()) { err.textContent = 'Please enter your e-mail address.'; return; }
      start.disabled = true; err.textContent = '';
      request('POST', '/sessions', { name: name.value.trim() || null, email: email.value.trim() || null, message: first.value.trim() || null,
        pageUrl: location.href, locale: navigator.language })
        .then(function (s) {
          state.conversationId = s.conversationId; state.token = s.visitorToken; save();
          applyState({ status: s.status, messages: s.messages });
          showChat(); connect();
        })
        .catch(function (ex) { err.textContent = ex.message; })
        .then(function () { start.disabled = false; });
    } }, [
      el('p', { text: 'Tell us a little about yourself so we can help.' }),
      el('label', {}, [document.createTextNode('Name'), name]),
      el('label', {}, [document.createTextNode('E-mail'), email]),
      el('label', {}, [document.createTextNode('Message'), first]),
      err, start
    ]);
    return form;
  }

  function showChat() {
    prechat.style.display = 'none';
    log.style.display = typing.style.display = actions.style.display = compose.style.display = '';
    input.focus();
  }

  function showPrechat() {
    prechat.style.display = '';
    log.style.display = typing.style.display = actions.style.display = compose.style.display = 'none';
  }

  function toggle() {
    state.open = !state.open;
    panel.classList.toggle('open', state.open);
    launcher.setAttribute('aria-expanded', String(state.open));
    if (state.open) { badge.style.display = 'none'; badge.textContent = ''; (state.conversationId ? input : prechat.querySelector('input')).focus(); }
  }

  // ───────────── messages ─────────────
  function render(m) {
    if (!m || state.seen[m.id]) return;
    state.seen[m.id] = true;
    state.messages.push(m);
    if (!state.lastAt || m.at > state.lastAt) state.lastAt = m.at;
    var who = m.author === 'visitor' ? 'You' : m.author === 'bot' ? 'Assistant' : m.author === 'system' ? '' : (m.authorName || 'Agent');
    var bubble = el('div', { class: 'msg ' + (m.author || 'system') });
    if (who && m.author !== 'system') bubble.appendChild(el('span', { class: 'who', text: who }));
    bubble.appendChild(document.createTextNode(m.text || ''));
    if (m.citations && m.citations.length) {
      var cites = el('div', { class: 'cites' });
      m.citations.forEach(function (c) {
        if (c.url && /^https?:|^\//.test(c.url)) cites.appendChild(el('a', { href: c.url, target: '_blank', rel: 'noopener noreferrer', text: '[' + c.index + '] ' + c.title }));
        else cites.appendChild(el('span', { text: '[' + c.index + '] ' + c.title + ' ' }));
        cites.appendChild(document.createTextNode(' '));
      });
      bubble.appendChild(cites);
    }
    log.appendChild(bubble);
    log.scrollTop = log.scrollHeight;
    if (!state.open && m.author !== 'visitor') { badge.style.display = 'block'; badge.textContent = String((parseInt(badge.textContent || '0', 10) || 0) + 1); }
  }

  function applyState(s) {
    if (!s) return;
    if (s.status) setStatus(s.status, s.agentName);
    if (typeof s.identityVerified === 'boolean') { state.verified = s.identityVerified; verifyBtn.style.display = state.verified || !state.config || !state.config.identityVerificationEnabled ? 'none' : ''; }
    (s.messages || []).forEach(render);
  }

  function setStatus(status, agentName) {
    state.status = status;
    if (agentName !== undefined) state.agentName = agentName;
    var text = status === 'BotActive' ? 'Assistant · ask for a person any time'
      : status === 'WaitingForAgent' ? 'Connecting you to an agent…'
      : status === 'AgentActive' ? ('Chatting with ' + (state.agentName || 'an agent'))
      : status === 'Closed' ? 'Chat ended' : 'Online';
    subtitle.textContent = text;
    humanBtn.style.display = status === 'BotActive' ? '' : 'none';
    var closed = status === 'Closed';
    input.disabled = sendBtn.disabled = closed;
    if (closed) { stopRealtime(); showRating(); }
  }

  function showRating() {
    if (root.querySelector('.rating')) return;
    var box = el('div', { class: 'rating', role: 'group', 'aria-label': 'Rate this chat' });
    [1, 2, 3, 4, 5].forEach(function (n) {
      box.appendChild(el('button', { type: 'button', text: n + ' ★', 'aria-label': n + ' of 5', onclick: function () {
        request('POST', '/sessions/' + state.conversationId + '/rating', { rating: n }).then(function () { box.textContent = 'Thanks for your feedback!'; });
      } }));
    });
    var restart = el('button', { type: 'button', text: 'Start a new chat', onclick: function () {
      forget(); state.conversationId = state.token = null; state.messages = []; state.seen = {}; state.lastAt = null;
      log.textContent = ''; box.remove(); restart.remove(); input.disabled = sendBtn.disabled = false; showPrechat();
    } });
    log.appendChild(box);
    actions.appendChild(restart);
  }

  function onSend(e) {
    if (e && e.preventDefault) e.preventDefault();
    var text = input.value.trim();
    if (!text || !state.conversationId) return;
    input.value = ''; sendBtn.disabled = true;
    signalTyping(false);
    request('POST', '/sessions/' + state.conversationId + '/messages', { text: text })
      .then(applyState)
      .catch(function (ex) { render({ id: 'err-' + Date.now(), author: 'system', text: ex.message, at: new Date().toISOString() }); if (ex.status === 403) expire(); })
      .then(function () { sendBtn.disabled = state.status === 'Closed'; input.focus(); });
  }

  function handoff() {
    request('POST', '/sessions/' + state.conversationId + '/handoff').then(applyState).catch(function () { /* ignore */ });
  }

  function endChat() {
    if (!state.conversationId || state.status === 'Closed') { toggle(); return; }
    if (!confirm('End this chat?')) return;
    request('POST', '/sessions/' + state.conversationId + '/close').then(applyState).catch(function () { /* ignore */ });
  }

  function verify() {
    var email = prompt('Enter your e-mail address. We will send you a 6-digit code.');
    if (!email) return;
    request('POST', '/sessions/' + state.conversationId + '/verify', { email: email }).then(function (r) {
      var code = prompt((r.demoCode ? '[Demo] Your code is ' + r.demoCode + '. ' : '') + 'Enter the code we e-mailed to you:');
      if (!code) return;
      return request('POST', '/sessions/' + state.conversationId + '/verify/confirm', { code: code }).then(function (c) {
        render({ id: 'v-' + Date.now(), author: 'system', text: c.message, at: new Date().toISOString() });
        if (c.status === 'verified') { state.verified = true; verifyBtn.style.display = 'none'; }
      });
    }).catch(function (ex) { render({ id: 'v-' + Date.now(), author: 'system', text: ex.message, at: new Date().toISOString() }); });
  }

  function expire() { forget(); stopRealtime(); state.conversationId = state.token = null; showPrechat(); }

  // ───────────── realtime: SignalR JSON protocol over WebSocket, polling fallback ─────────────
  function connect() {
    stopRealtime();
    if (!('WebSocket' in window)) { startPolling(); return; }
    var url = base.replace(/^http/, 'ws') + '/hubs/chat?conversation=' + encodeURIComponent(state.conversationId) + '&visitor_token=' + encodeURIComponent(state.token);
    var ws;
    try { ws = new WebSocket(url); } catch (e) { startPolling(); return; }
    state.socket = ws;
    var handshaken = false;
    ws.onopen = function () {
      ws.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
      state.pingTimer = setInterval(function () { if (ws.readyState === 1) ws.send(JSON.stringify({ type: 6 }) + RS); }, 15000);
    };
    ws.onmessage = function (ev) {
      String(ev.data).split(RS).forEach(function (frame) {
        if (!frame) return;
        var msg; try { msg = JSON.parse(frame); } catch (e) { return; }
        if (!handshaken) { handshaken = true; if (msg.error) { ws.close(); } return; }
        if (msg.type === 1) {
          var arg = (msg.arguments || [])[0] || {};
          if (msg.target === 'chat.message') render(arg);
          else if (msg.target === 'chat.status') applyState({ status: arg.status, agentName: arg.agentName, identityVerified: arg.identityVerified });
          else if (msg.target === 'chat.typing') showTyping(arg);
        } else if (msg.type === 7) { ws.close(); }
      });
    };
    ws.onclose = function () {
      clearInterval(state.pingTimer);
      if (state.socket !== ws) return;
      state.socket = null;
      if (state.conversationId && state.status !== 'Closed') { startPolling(); setTimeout(function () { if (state.conversationId && !state.socket) connect(); }, 15000); }
    };
  }

  function stopRealtime() {
    if (state.socket) { var s = state.socket; state.socket = null; try { s.close(); } catch (e) { /* ignore */ } }
    clearInterval(state.pingTimer); clearInterval(state.pollTimer); state.pollTimer = null;
  }

  function startPolling() {
    if (state.pollTimer) return;
    state.pollTimer = setInterval(function () {
      if (!state.conversationId) return;
      request('GET', '/sessions/' + state.conversationId + (state.lastAt ? '?after=' + encodeURIComponent(state.lastAt) : ''))
        .then(applyState).catch(function (ex) { if (ex.status === 403) expire(); });
    }, 4000);
  }

  var typingHide;
  function showTyping(t) {
    if (!t || t.from === 'visitor') return;
    typing.textContent = t.isTyping ? (t.name || 'Agent') + ' is typing…' : '';
    clearTimeout(typingHide);
    if (t.isTyping) typingHide = setTimeout(function () { typing.textContent = ''; }, 6000);
  }

  function signalTyping(on) {
    if (!state.conversationId) return;
    if (on && state.typingTimer) return;
    clearTimeout(state.typingTimer);
    state.typingTimer = on ? setTimeout(function () { state.typingTimer = null; }, 3000) : null;
    if (state.socket && state.socket.readyState === 1) state.socket.send(JSON.stringify({ type: 1, target: 'Typing', arguments: [on] }) + RS);
  }

  // ───────────── boot ─────────────
  function applyBrand(primary, accent) {
    host.style.setProperty('--sd-primary', primary || '#2563eb');
    host.style.setProperty('--sd-accent', accent || '#0ea5e9');
  }

  applyBrand();
  showPrechat();
  (document.body || document.documentElement).appendChild(host);

  request('GET', '/config').then(function (cfg) {
    state.config = cfg;
    title.textContent = cfg.title;
    applyBrand(cfg.primaryColor, cfg.accentColor);
    subtitle.textContent = cfg.agentsOnline ? 'We typically reply in a few minutes' : (cfg.offlineMessage || 'We are offline — leave a message');
    verifyBtn.style.display = cfg.identityVerificationEnabled ? '' : 'none';
    if (cfg.brandSlug) {
      fetch(base + '/api/v1/portal/' + encodeURIComponent(cfg.brandSlug) + '/config', { mode: 'cors', credentials: 'omit' })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (p) { if (p) applyBrand(p.primaryColor, p.accentColor); })
        .catch(function () { /* brand colours from the chat config are already applied */ });
    }
    var saved = load();
    if (saved && saved.conversationId && saved.token) {
      state.conversationId = saved.conversationId; state.token = saved.token;
      return request('GET', '/sessions/' + saved.conversationId).then(function (s) {
        applyState(s); showChat(); if (s.status !== 'Closed') connect();
      }).catch(function () { expire(); });
    }
  }).then(function () {
    if (script.getAttribute('data-open') === 'true' && !state.open) toggle();
  }).catch(function (ex) {
    console.warn('SupportDesk chat unavailable:', ex.message);
    host.remove();
  });
})();
