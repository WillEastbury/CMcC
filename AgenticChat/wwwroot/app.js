/* ── CMcC Agentic Chat – frontend ─────────────────────────────────────────── */

const API = '/api';

// ── State ──────────────────────────────────────────────────────────────────────
let userGuid = null;
let allSessions = [];       // summary objects from GET /api/sessions/{userGuid}
let openTabIds = [];        // session IDs currently in the tab bar
let currentSessionId = null;
let sessionCache = {};      // sessionId → full ChatSession (with messages)

// ── Bootstrap ─────────────────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', () => {
  resolveUserGuid();
  document.getElementById('btn-new-session').addEventListener('click', createNewSession);
  document.getElementById('btn-send').addEventListener('click', sendMessage);

  const input = document.getElementById('msg-input');
  input.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      sendMessage();
    }
  });
  input.addEventListener('input', () => autoResizeTextarea(input));
});

// ── User GUID management ───────────────────────────────────────────────────────

function resolveUserGuid() {
  // Allow /chat/{guid} URL to set / restore the localStorage value
  const pathMatch = window.location.pathname.match(
    /^\/chat\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i
  );
  if (pathMatch) {
    userGuid = pathMatch[1].toLowerCase();
    localStorage.setItem('userGuid', userGuid);
    // Clean the URL (optional – prevents confusion on refresh)
    history.replaceState(null, '', '/');
  } else {
    userGuid = localStorage.getItem('userGuid');
    if (!userGuid) {
      userGuid = crypto.randomUUID();
      localStorage.setItem('userGuid', userGuid);
    }
  }

  document.getElementById('user-guid-label').textContent =
    `ID: ${userGuid.slice(0, 8)}…`;

  loadSessions();
}

// ── Session list ───────────────────────────────────────────────────────────────

async function loadSessions() {
  try {
    const res = await fetch(`${API}/sessions/${userGuid}`);
    if (!res.ok) return;
    allSessions = await res.json();
    renderSidebar();
  } catch { /* network error – sidebar stays empty */ }
}

function renderSidebar() {
  const list = document.getElementById('session-list');
  const empty = document.getElementById('empty-sidebar');

  if (allSessions.length === 0) {
    empty.style.display = 'flex';
    // Remove any previously rendered items (keep the empty placeholder)
    [...list.querySelectorAll('.session-item')].forEach(el => el.remove());
    return;
  }
  empty.style.display = 'none';

  // Remove items no longer in list
  const ids = new Set(allSessions.map(s => s.id));
  [...list.querySelectorAll('.session-item')].forEach(el => {
    if (!ids.has(el.dataset.id)) el.remove();
  });

  // Update / insert items
  allSessions.forEach(session => {
    let el = list.querySelector(`.session-item[data-id="${session.id}"]`);
    if (!el) {
      el = document.createElement('div');
      el.className = 'session-item';
      el.dataset.id = session.id;
      el.addEventListener('click', () => openSession(session.id));
      list.appendChild(el);
    }
    el.classList.toggle('active', session.id === currentSessionId);
    el.innerHTML = `
      <div class="session-title">${escHtml(session.title)}</div>
      <div class="session-meta">
        <i class="bi bi-chat-dots me-1"></i>${session.messageCount} msgs ·
        ${relativeTime(session.updatedAt)}
      </div>`;
  });
}

// ── Tab bar ────────────────────────────────────────────────────────────────────

function renderTabs() {
  const tabList = document.getElementById('tab-list');
  tabList.innerHTML = '';

  openTabIds.forEach(id => {
    const session = allSessions.find(s => s.id === id)
      || { id, title: id.slice(0, 8) + '…' };

    const li = document.createElement('li');
    li.className = 'nav-item d-inline-flex align-items-center';
    li.dataset.id = id;

    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'nav-link' + (id === currentSessionId ? ' active' : '');
    btn.textContent = truncate(session.title, 24);
    btn.addEventListener('click', () => switchToTab(id));

    const closeBtn = document.createElement('button');
    closeBtn.type = 'button';
    closeBtn.className = 'btn-close btn-close-white ms-1';
    closeBtn.setAttribute('aria-label', 'Close tab');
    closeBtn.addEventListener('click', e => { e.stopPropagation(); closeTab(id); });

    li.appendChild(btn);
    li.appendChild(closeBtn);
    tabList.appendChild(li);
  });
}

function switchToTab(id) {
  currentSessionId = id;
  renderTabs();
  renderSidebar();
  renderChat();
}

function closeTab(id) {
  openTabIds = openTabIds.filter(t => t !== id);
  if (currentSessionId === id) {
    currentSessionId = openTabIds.length > 0 ? openTabIds[openTabIds.length - 1] : null;
  }
  renderTabs();
  renderSidebar();
  renderChat();
}

// ── Open / create sessions ─────────────────────────────────────────────────────

async function openSession(sessionId) {
  if (!openTabIds.includes(sessionId)) {
    openTabIds.push(sessionId);
  }
  currentSessionId = sessionId;
  renderTabs();
  renderSidebar();
  await renderChat();
}

async function createNewSession() {
  const btn = document.getElementById('btn-new-session');
  btn.disabled = true;
  btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Creating…';

  try {
    const res = await fetch(`${API}/sessions/${userGuid}`, { method: 'POST' });
    if (!res.ok) throw new Error(await res.text());
    const session = await res.json();

    // Add to summary list
    allSessions.unshift({
      id: session.id,
      title: session.title,
      updatedAt: session.updatedAt,
      messageCount: session.messages.length,
    });
    sessionCache[session.id] = session;

    if (!openTabIds.includes(session.id)) openTabIds.push(session.id);
    currentSessionId = session.id;

    renderSidebar();
    renderTabs();
    renderChat();
  } catch (err) {
    showToast('Failed to create session: ' + err.message, 'danger');
  } finally {
    btn.disabled = false;
    btn.innerHTML = '<i class="bi bi-plus-lg me-1"></i>New Session';
  }
}

// ── Chat rendering ─────────────────────────────────────────────────────────────

async function renderChat() {
  const msgContainer = document.getElementById('messages');
  const emptyChat    = document.getElementById('empty-chat');
  const input        = document.getElementById('msg-input');
  const sendBtn      = document.getElementById('btn-send');

  if (!currentSessionId) {
    emptyChat.style.display = 'flex';
    input.disabled = true;
    sendBtn.disabled = true;
    msgContainer.innerHTML = '';
    msgContainer.appendChild(emptyChat);
    return;
  }

  emptyChat.style.display = 'none';
  input.disabled = false;
  sendBtn.disabled = false;
  input.focus();

  // Load full session if not cached
  if (!sessionCache[currentSessionId]) {
    try {
      const res = await fetch(`${API}/sessions/${userGuid}/${currentSessionId}`);
      if (!res.ok) return;
      sessionCache[currentSessionId] = await res.json();
    } catch { return; }
  }

  const session = sessionCache[currentSessionId];

  // Rebuild message list
  msgContainer.innerHTML = '';
  msgContainer.appendChild(emptyChat); // keep in DOM (hidden)

  if (session.messages.length === 0) {
    emptyChat.style.display = 'flex';
    emptyChat.innerHTML = `
      <i class="bi bi-chat-left-text"></i>
      <span>Session started. Say something!</span>`;
    return;
  }

  session.messages.forEach(m => appendBubble(m.role, m.content, m.timestamp));
  scrollToBottom();
}

function appendBubble(role, content, timestamp) {
  const msgContainer = document.getElementById('messages');

  const wrap = document.createElement('div');
  wrap.className = `bubble-wrap ${role}`;

  const bubble = document.createElement('div');
  bubble.className = 'bubble';

  // Render markdown for assistant messages
  if (role === 'assistant' && typeof marked !== 'undefined') {
    bubble.innerHTML = marked.parse(content);
  } else {
    bubble.textContent = content;
  }

  if (timestamp) {
    const time = document.createElement('span');
    time.className = 'msg-time';
    time.textContent = new Date(timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    bubble.appendChild(time);
  }

  wrap.appendChild(bubble);
  msgContainer.appendChild(wrap);
}

function scrollToBottom() {
  const msgContainer = document.getElementById('messages');
  msgContainer.scrollTop = msgContainer.scrollHeight;
}

// ── Send message ───────────────────────────────────────────────────────────────

async function sendMessage() {
  const input = document.getElementById('msg-input');
  const text  = input.value.trim();
  if (!text || !currentSessionId) return;

  input.value = '';
  autoResizeTextarea(input);
  input.disabled = true;
  document.getElementById('btn-send').disabled = true;

  // Optimistic UI
  const emptyChat = document.getElementById('empty-chat');
  emptyChat.style.display = 'none';
  appendBubble('user', text, new Date().toISOString());
  scrollToBottom();
  showTyping();

  try {
    const res = await fetch(
      `${API}/sessions/${userGuid}/${currentSessionId}/messages`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ content: text }),
      }
    );

    if (!res.ok) throw new Error(await res.text());
    const { content, sessionTitle } = await res.json();

    hideTyping();
    appendBubble('assistant', content, new Date().toISOString());
    scrollToBottom();

    // Update cache and sidebar
    if (sessionCache[currentSessionId]) {
      sessionCache[currentSessionId].messages.push(
        { role: 'user',      content: text,    timestamp: new Date().toISOString() },
        { role: 'assistant', content,           timestamp: new Date().toISOString() }
      );
      sessionCache[currentSessionId].title = sessionTitle;
    }

    const summary = allSessions.find(s => s.id === currentSessionId);
    if (summary) {
      summary.title        = sessionTitle;
      summary.updatedAt    = new Date().toISOString();
      summary.messageCount = (summary.messageCount || 0) + 2;
      // Keep newest-first order
      allSessions.sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt));
    }

    renderSidebar();
    renderTabs();
  } catch (err) {
    hideTyping();
    showToast('Error: ' + err.message, 'danger');
  } finally {
    input.disabled = false;
    document.getElementById('btn-send').disabled = false;
    input.focus();
  }
}

// ── Typing indicator ───────────────────────────────────────────────────────────

function showTyping() {
  document.getElementById('typing').style.display = 'block';
  scrollToBottom();
}
function hideTyping() {
  document.getElementById('typing').style.display = 'none';
}

// ── Toast notifications ────────────────────────────────────────────────────────

function showToast(message, type = 'info') {
  const container = document.getElementById('toast-container') || createToastContainer();
  const toastEl = document.createElement('div');
  toastEl.className = `toast align-items-center text-bg-${type} border-0`;
  toastEl.setAttribute('role', 'alert');
  toastEl.innerHTML = `
    <div class="d-flex">
      <div class="toast-body">${escHtml(message)}</div>
      <button type="button" class="btn-close btn-close-white me-2 m-auto"
              data-bs-dismiss="toast"></button>
    </div>`;
  container.appendChild(toastEl);
  const t = new bootstrap.Toast(toastEl, { autohide: true, delay: 5000 });
  t.show();
  toastEl.addEventListener('hidden.bs.toast', () => toastEl.remove());
}

function createToastContainer() {
  const c = document.createElement('div');
  c.id = 'toast-container';
  c.className = 'toast-container position-fixed bottom-0 end-0 p-3';
  c.style.zIndex = 9999;
  document.body.appendChild(c);
  return c;
}

// ── Utilities ──────────────────────────────────────────────────────────────────

function escHtml(str) {
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function truncate(str, max) {
  return str.length > max ? str.slice(0, max) + '…' : str;
}

function relativeTime(isoString) {
  const diff = Math.round((Date.now() - new Date(isoString)) / 1000);
  if (diff < 60)  return `${diff}s ago`;
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
  return new Date(isoString).toLocaleDateString();
}

function autoResizeTextarea(el) {
  el.style.height = 'auto';
  el.style.height = Math.min(el.scrollHeight, 140) + 'px';
}
