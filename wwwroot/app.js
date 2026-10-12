// Panel web del Servidor FTP: API REST + SignalR para datos en vivo.
const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fmtBytes = n => { const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0; n = Number(n) || 0; while (n >= 1024 && i < 4) { n /= 1024; i++; } return n.toFixed(i ? 1 : 0) + ' ' + u[i]; };
const fmtTime = t => new Date(t).toLocaleString();
const fmtTimeShort = t => new Date(t).toLocaleTimeString();

let hub = null, speed = [];      // speed: [{up, down}] últimos 120 s
const MAX_SAMPLES = 120;

/** Llama a la API; si la sesión caducó vuelve a la pantalla de login. */
async function api(path, method = 'GET', body) {
  const r = await fetch(path, { method, headers: body ? { 'Content-Type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined });
  if (r.status === 401 && path !== '/api/login') { showLogin(); throw new Error('Sesión caducada'); }
  const data = r.headers.get('content-type')?.includes('json') ? await r.json() : null;
  if (!r.ok) throw new Error(data?.error || 'Error ' + r.status);
  return data;
}

// ---------------------------------------------------------------- login
function showLogin() { if (hub) { hub.stop(); hub = null; } $('app').classList.add('hidden'); $('login').classList.remove('hidden'); $('lPass').value = ''; }
async function showApp() { $('login').classList.add('hidden'); $('app').classList.remove('hidden'); await refreshAll(); connectHub(); }
async function doLogin() {
  $('lErr').textContent = '';
  try { await api('/api/login', 'POST', { user: $('lUser').value, password: $('lPass').value }); await showApp(); }
  catch (e) { $('lErr').textContent = e.message; }
}
$('lBtn').onclick = doLogin;
$('lPass').onkeydown = e => { if (e.key === 'Enter') doLogin(); };
$('logout').onclick = async () => { await api('/api/logout', 'POST'); showLogin(); };

// ---------------------------------------------------------------- pestañas
document.querySelectorAll('nav button[data-tab]').forEach(b => b.onclick = () => {
  document.querySelectorAll('nav button[data-tab]').forEach(x => x.classList.toggle('active', x === b));
  document.querySelectorAll('.tab').forEach(t => t.classList.toggle('hidden', t.id !== b.dataset.tab));
  ({ users: loadUsers, history: loadHistory, stats: loadStats, settings: loadSettings }[b.dataset.tab] || (() => { }))();
});

// ---------------------------------------------------------------- SignalR
function connectHub() {
  hub = new signalR.HubConnectionBuilder().withUrl('/hub').withAutomaticReconnect().build();
  hub.on('tick', onTick);
  hub.on('log', addLive);
  hub.on('status', setStatus);
  hub.start().catch(() => { });
}
function setStatus(s) {
  $('pill').textContent = s.running ? 'En marcha' : 'Detenido';
  $('pill').className = 'pill ' + (s.running ? 'on' : 'off');
  $('info').textContent = `Puerto FTP ${s.ftpPort} · Panel ${s.webPort}` + (s.advertising ? ` · Anunciado como ${s.hostName}.local` : '');
  $('bStart').disabled = s.running; $('bStop').disabled = !s.running; $('bRestart').disabled = !s.running;
}
function onTick(t) {
  $('pill').textContent = t.running ? 'En marcha' : 'Detenido';
  $('pill').className = 'pill ' + (t.running ? 'on' : 'off');
  speed.push({ up: t.upSpeed, down: t.downSpeed }); if (speed.length > MAX_SAMPLES) speed.shift();
  $('speedTxt').textContent = `▲ ${fmtBytes(t.upSpeed)}/s   ▼ ${fmtBytes(t.downSpeed)}/s`;
  drawChart();
  const k = t.kpi;
  $('kpis').innerHTML = [['Sesiones', t.sessions.length], ['Conexiones hoy', k.connections], ['Subidas', k.uploads + ' (' + fmtBytes(k.uploadBytes) + ')'],
    ['Descargas', k.downloads + ' (' + fmtBytes(k.downloadBytes) + ')'], ['Errores', k.errors]]
    .map(([l, v]) => `<div class="kpi"><b>${esc(v)}</b><span>${l}</span></div>`).join('');
  $('tSessions').tBodies[0].innerHTML = t.sessions.map(s => `<tr><td>${s.id}</td><td>${esc(s.user || '—')}</td><td>${esc(s.remote)}</td><td>${fmtTimeShort(s.connectedAt)}</td><td>${esc(s.lastCommand)}</td><td>${esc(s.directory)}</td><td>${fmtBytes(s.bytesIn)}</td><td>${fmtBytes(s.bytesOut)}</td><td><button class="danger" data-kick="${s.id}">Expulsar</button></td></tr>`).join('')
    || '<tr><td colspan="9" class="muted">Sin sesiones activas</td></tr>';
}
$('tSessions').onclick = async e => { const id = e.target.dataset.kick; if (id && confirm('¿Expulsar la sesión ' + id + '?')) await api('/api/sessions/' + id, 'DELETE').catch(x => alert(x.message)); };

function drawChart() {
  const c = $('chart'), w = c.clientWidth, h = 150, dpr = window.devicePixelRatio || 1;
  c.width = w * dpr; c.height = h * dpr;
  const g = c.getContext('2d'); g.scale(dpr, dpr); g.clearRect(0, 0, w, h);
  const max = Math.max(1024, ...speed.map(s => Math.max(s.up, s.down)));
  const line = (key, color) => {
    g.beginPath(); g.strokeStyle = color; g.lineWidth = 2;
    speed.forEach((s, i) => { const x = (i / (MAX_SAMPLES - 1)) * w, y = h - 4 - (s[key] / max) * (h - 12); i ? g.lineTo(x, y) : g.moveTo(x, y); });
    g.stroke();
  };
  line('down', '#2563eb'); line('up', '#16a34a');
  g.fillStyle = '#888'; g.font = '11px sans-serif'; g.fillText(fmtBytes(max) + '/s', 4, 12);
}

// ---------------------------------------------------------------- panel
const logRow = e => `<tr class="${esc(e.category)}"><td>${fmtTime(e.time)}</td><td>${esc(e.category)}</td><td>${esc(e.user)}</td><td>${esc(e.remote)}</td><td class="msg">${esc(e.message)}</td><td>${esc(e.file)}</td><td>${e.bytes ? fmtBytes(e.bytes) : ''}</td><td>${e.durationMs ? (e.durationMs / 1000).toFixed(1) + ' s' : ''}</td></tr>`;
const liveRow = e => `<tr class="${esc(e.category)}"><td>${fmtTimeShort(e.time)}</td><td>${esc(e.category)}</td><td>${esc(e.user)}</td><td class="msg">${esc(e.message)} ${esc(e.file)}</td></tr>`;
function addLive(e) { const b = $('tLive').tBodies[0]; b.insertAdjacentHTML('afterbegin', liveRow(e)); while (b.rows.length > 50) b.deleteRow(-1); }
async function refreshAll() {
  setStatus(await api('/api/status'));
  const recent = await api('/api/logs/recent?count=50');
  $('tLive').tBodies[0].innerHTML = recent.reverse().map(liveRow).join('');
}
for (const [id, act] of [['bStart', 'start'], ['bStop', 'stop'], ['bRestart', 'restart']])
  $(id).onclick = async () => { try { setStatus(await api('/api/server/' + act, 'POST')); } catch (e) { alert(e.message); } };

// ---------------------------------------------------------------- usuarios
let editing = null, cfg = null, rootTouched = false;
/** Separador de rutas del servidor (según la carpeta base: Windows "\\" o Linux "/"). */
const sep = () => (cfg?.baseFolder || '').includes('\\') ? '\\' : '/';
/** Autorellena la carpeta raíz como "carpeta base + nombre" mientras se crea un usuario. */
function autoRoot() {
  if (editing !== null || rootTouched || !cfg) return;
  const n = $('uName').value.trim();
  $('uRoot').value = n ? cfg.baseFolder.replace(/[\\/]+$/, '') + sep() + n : '';
}
async function loadUsers() {
  const list = await api('/api/users');
  $('tUsers').tBodies[0].innerHTML = list.map(u => `<tr><td>${esc(u.username)}</td><td>${esc(u.rootPath)}</td><td>${u.readOnly ? 'Solo lectura' : 'Lectura y escritura'}</td><td>${u.hasPassword ? 'Definida' : '—'}</td><td><button data-edit="${esc(u.username)}">Editar</button> <button class="danger" data-del="${esc(u.username)}">Eliminar</button></td></tr>`).join('');
  $('tUsers').dataset.json = JSON.stringify(list);
}
async function openUser(u) {
  cfg = await api('/api/settings').catch(() => null);
  rootTouched = false;
  editing = u?.username ?? null;
  $('uTitle').textContent = u ? 'Editar usuario' : 'Nuevo usuario';
  $('uName').value = u?.username ?? ''; $('uName').disabled = !!u;
  $('uPass').value = ''; $('uPass').placeholder = u ? 'Vacía = no cambiar' : '';
  $('uRoot').value = u?.rootPath ?? ''; $('uRO').checked = !!u?.readOnly; $('uErr').textContent = '';
  $('uRoot').placeholder = cfg ? (cfg.restrictToBase ? 'Dentro de ' + cfg.baseFolder : 'Ruta completa') : '';
  $('uDlg').showModal();
}
$('uAdd').onclick = () => openUser(null);
$('uName').oninput = autoRoot;
$('uRoot').oninput = () => { rootTouched = true; };
$('uCancel').onclick = () => $('uDlg').close();
$('uOk').onclick = async () => {
  const body = { username: $('uName').value.trim(), password: $('uPass').value, rootPath: $('uRoot').value.trim(), readOnly: $('uRO').checked };
  try { await api(editing ? '/api/users/' + encodeURIComponent(editing) : '/api/users', editing ? 'PUT' : 'POST', body); $('uDlg').close(); loadUsers(); }
  catch (e) { $('uErr').textContent = e.message; }
};
$('tUsers').onclick = async e => {
  const list = JSON.parse($('tUsers').dataset.json || '[]');
  if (e.target.dataset.edit) openUser(list.find(u => u.username === e.target.dataset.edit));
  if (e.target.dataset.del && confirm('¿Eliminar a ' + e.target.dataset.del + '?')) { try { await api('/api/users/' + encodeURIComponent(e.target.dataset.del), 'DELETE'); loadUsers(); } catch (x) { alert(x.message); } }
};

// ---------------------------------------------------------------- historial y estadísticas
function histQuery() { const p = new URLSearchParams({ days: $('hDays').value, category: $('hCat').value, user: $('hUser').value, q: $('hText').value }); return p.toString(); }
async function loadHistory() {
  const list = await api('/api/logs?max=1000&' + histQuery());
  $('tHist').tBodies[0].innerHTML = list.reverse().map(logRow).join('');
  $('hCount').textContent = list.length + ' eventos (máximo 1000 mostrados)';
  $('hCsv').href = '/api/logs/export?' + histQuery();
}
$('hGo').onclick = loadHistory;
async function loadStats() {
  const list = await api('/api/stats?days=' + $('sDays').value);
  $('tStats').tBodies[0].innerHTML = list.map(s => `<tr><td>${esc(s.user)}</td><td>${s.connections}</td><td>${s.uploads}</td><td>${s.downloads}</td><td>${s.files}</td><td>${fmtBytes(s.uploadBytes)}</td><td>${fmtBytes(s.downloadBytes)}</td><td>${s.errors}</td><td>${s.lastActivity.startsWith('0001') ? '—' : fmtTime(s.lastActivity)}</td></tr>`).join('');
}
$('sDays').onchange = loadStats;

// ---------------------------------------------------------------- ajustes
async function loadSettings() {
  const s = await api('/api/settings');
  $('sFtpPort').value = s.ftpPort; $('sPMin').value = s.passivePortMin; $('sPMax').value = s.passivePortMax; $('sPExt').value = s.passiveExternalIp ?? '';
  $('sBase').value = s.baseFolder ?? ''; $('sRestrict').checked = s.restrictToBase; $('sRet').value = s.retentionDays; $('sBind').value = s.webBindAddress; $('sWeb').value = s.webPort; $('sName').value = s.advertiseName;
  $('sAuto').checked = s.autoStartFtp; $('sAdv').checked = s.advertiseEnabled; $('sMsg').textContent = '';
}
$('sSave').onclick = async () => {
  const body = { ftpPort: +$('sFtpPort').value, passivePortMin: +$('sPMin').value, passivePortMax: +$('sPMax').value, passiveExternalIp: $('sPExt').value,
    retentionDays: +$('sRet').value, webBindAddress: $('sBind').value, webPort: +$('sWeb').value, advertiseName: $('sName').value, baseFolder: $('sBase').value, restrictToBase: $('sRestrict').checked,
    autoStartFtp: $('sAuto').checked, advertiseEnabled: $('sAdv').checked };
  try { const r = await api('/api/settings', 'PUT', body); $('sMsg').textContent = r.restartRequired ? 'Guardado. Reinicia el servicio para cambiar el puerto o la dirección del panel.' : 'Guardado.'; }
  catch (e) { $('sMsg').textContent = e.message; }
};
$('pSave').onclick = async () => {
  try { await api('/api/password', 'POST', { current: $('pCur').value, new: $('pNew').value }); alert('Contraseña cambiada. Vuelve a iniciar sesión.'); showLogin(); }
  catch (e) { $('pMsg').textContent = e.message; }
};

// ---------------------------------------------------------------- arranque
api('/api/me').then(showApp).catch(showLogin);
window.addEventListener('resize', drawChart);
