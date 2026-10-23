/*
 * Agent Stage — AgentZero Lite web plugin.
 *
 * Polls window.zero.invoke('sessions.list') (the host's ACTIVE SESSIONS: every workspace, its
 * running terminals, each one's agent and detected state) and draws it as a live stage:
 *   · a zone per workspace, coloured from its name; the active workspace framed in teal (as in
 *     the sidebar);
 *   · an avatar per terminal — Claude ✳, Codex ◆, AgentOne ❖, Netclaw ✦, a shell ›_ —
 *     whose motion says what it is doing: orbiting sparks, a thinking ring and typing bars while
 *     working, a red pulse when it needs you, a burst when it finishes;
 *   · click an avatar → sessions.focus jumps to that terminal.
 * Outside the app (no bridge) or with ?demo in the URL it runs on simulated sessions.
 */
(() => {
  'use strict';

  const POLL_MS = 1500;
  const DEMO = !window.zero || /[?&]demo\b/.test(location.search);

  const AGENTS = {
    claude:   { color: '#E8885A', glyph: '✳', name: 'Claude' },
    codex:    { color: '#3CCB8F', glyph: '◆', name: 'Codex' },
    agentone: { color: '#B48CFF', glyph: '❖', name: 'AgentOne' },
    netclaw:  { color: '#F2C94C', glyph: '✦', name: 'Netclaw' },
    shell:    { color: '#8A93A3', glyph: '›_', name: 'Shell' },
  };
  const STATES = {
    working:     { color: '#E5C07B', label: 'working' },
    blocked:     { color: '#E06C75', label: 'needs you' },
    done:        { color: '#61AFEF', label: 'done' },
    'done-seen': { color: '#98C379', label: 'done' },
    idle:        { color: '#7F848E', label: 'idle' },
    running:     { color: '#A06CFF', label: 'running' },
  };
  const CODE_CHARS = ['{', '}', '<', '/>', ';', '()', '=>', '01', '#', '*', '[]', 'fn', '∑', 'λ'];

  // ── DOM ──────────────────────────────────────────────────────────────────
  const $ = (id) => document.getElementById(id);
  const canvas = $('as-canvas');
  const ctx = canvas.getContext('2d');
  const stageEl = $('as-stage');
  const emptyEl = $('as-empty');
  const tipEl = $('as-tip');
  const allBox = $('as-all');
  const calmBox = $('as-calm');

  const store = {
    get(k, d) { try { const v = localStorage.getItem('agent-stage.' + k); return v === null ? d : v === '1'; } catch { return d; } },
    set(k, v) { try { localStorage.setItem('agent-stage.' + k, v ? '1' : '0'); } catch { /* private window */ } },
  };
  allBox.checked = store.get('all', false);
  calmBox.checked = store.get('calm', false);
  allBox.addEventListener('change', () => { store.set('all', allBox.checked); layout(); });
  calmBox.addEventListener('change', () => store.set('calm', calmBox.checked));
  if (DEMO) $('as-mode').textContent = 'demo';

  // ── model ────────────────────────────────────────────────────────────────
  let data = { supported: true, workspaces: [] };
  const avatars = new Map();   // key → avatar (kept across polls so motion is continuous)
  let zones = [];
  let particles = [];
  let W = 0, H = 0, dpr = 1;
  let hover = null;
  let shake = 0;

  const keyOf = (ws, s) => `${ws.path}|${s.group}:${s.tab}|${s.title}`;

  function hueOf(text) {
    let h = 2166136261;
    for (let i = 0; i < text.length; i++) { h ^= text.charCodeAt(i); h = Math.imul(h, 16777619); }
    return Math.abs(h) % 360;
  }

  function apply(next) {
    data = next || { supported: false, workspaces: [] };
    const seen = new Set();
    for (const ws of data.workspaces || []) {
      for (const s of ws.sessions || []) {
        const key = keyOf(ws, s);
        seen.add(key);
        let a = avatars.get(key);
        if (!a) {
          a = { key, x: NaN, y: NaN, tx: 0, ty: 0, r: 22, born: now(), phase: Math.random() * 6.28,
                sparkT: 0, ringT: 0, state: s.state };
          avatars.set(key, a);
        } else if (a.state !== s.state) {
          transition(a, a.state, s.state);
        }
        Object.assign(a, { ws, session: s, agent: AGENTS[s.agent] ? s.agent : 'shell',
                           state: s.state, title: s.title, focused: !!s.focused, attention: !!s.needsAttention });
      }
    }
    for (const k of [...avatars.keys()]) if (!seen.has(k)) avatars.delete(k);
    updateStats();
    layout();
  }

  function transition(a, from, to) {
    if (!isFinite(a.x)) return;
    const ag = AGENTS[a.agent] || AGENTS.shell;
    if (to === 'done' || (from === 'working' && to === 'done-seen')) {
      burst(a.x, a.y, [STATES.done.color, ag.color, '#ffffff'], 46, 4.2);
      a.ringT = 0;
    } else if (to === 'blocked') {
      burst(a.x, a.y, [STATES.blocked.color, '#ffb4b4'], 28, 3.2);
      if (!calmBox.checked) shake = 8;
    } else if (to === 'working') {
      burst(a.x, a.y, [ag.color, STATES.working.color], 18, 2.4);
    }
  }

  function updateStats() {
    let working = 0, waiting = 0, done = 0, total = 0, wsWith = 0;
    for (const ws of data.workspaces || []) {
      if ((ws.sessions || []).length) wsWith++;
      for (const s of ws.sessions || []) {
        total++;
        if (s.state === 'working') working++;
        else if (s.state === 'blocked') waiting++;
        else if (s.state === 'done') done++;
      }
    }
    $('as-n-working').textContent = working;
    $('as-n-waiting').textContent = waiting;
    $('as-n-done').textContent = done;
    $('as-n-total').textContent = total;
    $('as-n-ws').textContent = wsWith;
  }

  // ── layout: a grid of zones, avatars in rows inside each ─────────────────
  function layout() {
    const shown = (data.workspaces || []).filter(ws => allBox.checked || ws.active || (ws.sessions || []).length);
    emptyEl.hidden = true;
    if (!data.supported) return showEmpty('This host does not share its sessions with plugins yet.<br>Agent Stage runs in the cross-platform (Avalonia) edition.');
    if (!shown.length) return showEmpty('No terminal is running.<br>Open one in a workspace — its agent appears here.');

    const pad = 14, gap = 14;
    const n = shown.length;
    const aspect = W / Math.max(1, H);
    let cols = Math.max(1, Math.round(Math.sqrt(n * aspect / 1.3)));
    cols = Math.min(cols, n);
    const rows = Math.ceil(n / cols);
    const zw = (W - pad * 2 - gap * (cols - 1)) / cols;
    const zh = (H - pad * 2 - gap * (rows - 1)) / rows;

    zones = shown.map((ws, i) => {
      const c = i % cols, r = Math.floor(i / cols);
      const z = { ws, x: pad + c * (zw + gap), y: pad + r * (zh + gap), w: zw, h: zh, hue: hueOf(ws.path || ws.name) };
      const list = (ws.sessions || []).map(s => avatars.get(keyOf(ws, s))).filter(Boolean);
      const innerTop = z.y + 40, innerH = z.h - 52;
      const per = Math.max(1, Math.floor(z.w / 120));
      const lines = Math.max(1, Math.ceil(list.length / per));
      const cell = Math.min(z.w / Math.min(per, Math.max(1, list.length)), innerH / lines);
      const r0 = Math.max(12, Math.min(30, cell * 0.24));
      list.forEach((a, j) => {
        const row = Math.floor(j / per), inRow = Math.min(per, list.length - row * per);
        const cw = z.w / inRow;
        a.tx = z.x + cw * (j % per + 0.5);
        a.ty = innerTop + (innerH / lines) * (row + 0.42);
        a.r = r0;
        if (!isFinite(a.x)) { a.x = a.tx; a.y = a.ty + 30; }
      });
      z.counts = {
        working: list.filter(a => a.state === 'working').length,
        need: list.filter(a => a.state === 'blocked').length,
      };
      return z;
    });
  }

  function showEmpty(html) { zones = []; emptyEl.innerHTML = html; emptyEl.hidden = false; }

  // ── particles ────────────────────────────────────────────────────────────
  function burst(x, y, colors, n, speed) {
    if (calmBox.checked) n = Math.ceil(n / 3);
    for (let i = 0; i < n; i++) {
      const ang = Math.random() * Math.PI * 2, v = speed * (0.4 + Math.random());
      particles.push({ x, y, vx: Math.cos(ang) * v, vy: Math.sin(ang) * v - 1, g: 0.06, life: 0,
                       max: 50 + Math.random() * 40, color: colors[i % colors.length], size: 1.5 + Math.random() * 2.5 });
    }
  }

  function spark(a, color) {
    const ang = -Math.PI / 2 + (Math.random() - 0.5) * 1.4;
    const v = 0.8 + Math.random() * 1.2;
    particles.push({ x: a.x + Math.cos(ang) * a.r, y: a.y + Math.sin(ang) * a.r, vx: Math.cos(ang) * v, vy: Math.sin(ang) * v,
                     g: -0.004, life: 0, max: 60 + Math.random() * 30, color,
                     text: CODE_CHARS[(Math.random() * CODE_CHARS.length) | 0], size: 9 + Math.random() * 4 });
  }

  // ── drawing ──────────────────────────────────────────────────────────────
  const now = () => performance.now() / 1000;
  const stars = Array.from({ length: 90 }, () => ({ x: Math.random(), y: Math.random(), z: 0.2 + Math.random() * 0.8 }));

  function background(t) {
    const g = ctx.createLinearGradient(0, 0, 0, H);
    g.addColorStop(0, '#0d0f14'); g.addColorStop(1, '#0a0c11');
    ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
    for (const s of stars) {
      const x = (s.x * W + t * 6 * s.z) % W, y = s.y * H;
      ctx.globalAlpha = 0.15 + 0.25 * s.z * (0.6 + 0.4 * Math.sin(t * 1.3 + s.x * 40));
      ctx.fillStyle = '#9fb3d1';
      ctx.fillRect(x, y, s.z * 1.6, s.z * 1.6);
    }
    ctx.globalAlpha = 1;
  }

  function roundRect(x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y); ctx.arcTo(x + w, y, x + w, y + h, r); ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r); ctx.arcTo(x, y, x + w, y, r); ctx.closePath();
  }

  function drawZone(z, t) {
    const busy = z.counts.working > 0;
    const hsl = (s, l, a) => `hsla(${z.hue},${s}%,${l}%,${a})`;
    // body
    const g = ctx.createLinearGradient(z.x, z.y, z.x, z.y + z.h);
    g.addColorStop(0, hsl(45, 16, busy ? 0.62 : 0.45)); g.addColorStop(1, hsl(40, 9, 0.35));
    roundRect(z.x, z.y, z.w, z.h, 12); ctx.fillStyle = g; ctx.fill();
    // energy waves while someone works
    if (busy) {
      ctx.save(); roundRect(z.x, z.y, z.w, z.h, 12); ctx.clip();
      for (let i = 0; i < 3; i++) {
        const p = ((t * 0.35 + i / 3) % 1);
        ctx.strokeStyle = hsl(80, 60, 0.18 * (1 - p));
        ctx.lineWidth = 2;
        ctx.beginPath();
        for (let x = 0; x <= z.w; x += 8) {
          const y = z.y + z.h * (1 - p) + Math.sin(x / 38 + t * 3 + i) * 6;
          x === 0 ? ctx.moveTo(z.x + x, y) : ctx.lineTo(z.x + x, y);
        }
        ctx.stroke();
      }
      ctx.restore();
    }
    // frame: teal for the active workspace (the sidebar's mark), the zone hue otherwise
    roundRect(z.x + 0.5, z.y + 0.5, z.w - 1, z.h - 1, 12);
    if (z.ws.active) {
      ctx.strokeStyle = '#4ec9b0'; ctx.lineWidth = 2.2;
      ctx.shadowColor = '#4ec9b0'; ctx.shadowBlur = 14;
    } else {
      ctx.strokeStyle = hsl(55, 45, busy ? 0.75 : 0.4); ctx.lineWidth = 1.2;
    }
    if (busy) { ctx.setLineDash([10, 8]); ctx.lineDashOffset = -t * 30; }
    ctx.stroke(); ctx.setLineDash([]); ctx.shadowBlur = 0;
    // header
    ctx.fillStyle = hsl(70, 70, 1); ctx.beginPath(); ctx.arc(z.x + 16, z.y + 19, 4.5, 0, 6.29); ctx.fill();
    ctx.font = '600 13px "Segoe UI", system-ui, sans-serif';
    ctx.fillStyle = z.ws.active ? '#4ec9b0' : '#d8dee9';
    ctx.textBaseline = 'middle'; ctx.textAlign = 'left';
    ctx.fillText(clip(z.ws.name, z.w - 130), z.x + 28, z.y + 19);
    ctx.font = '11px "Segoe UI", system-ui, sans-serif'; ctx.textAlign = 'right';
    const meta = [];
    if (z.counts.working) meta.push(`⚡ ${z.counts.working}`);
    if (z.counts.need) meta.push(`● ${z.counts.need}`);
    meta.push(`${(z.ws.sessions || []).length} tab${(z.ws.sessions || []).length === 1 ? '' : 's'}`);
    ctx.fillStyle = z.counts.need ? STATES.blocked.color : '#8a93a3';
    ctx.fillText(meta.join('  '), z.x + z.w - 12, z.y + 19);
  }

  function clip(text, max) {
    if (ctx.measureText(text).width <= max) return text;
    while (text.length > 1 && ctx.measureText(text + '…').width > max) text = text.slice(0, -1);
    return text + '…';
  }

  function drawAvatar(a, t, dt) {
    a.x += (a.tx - a.x) * Math.min(1, dt * 6);
    a.y += (a.ty - a.y) * Math.min(1, dt * 6);
    const ag = AGENTS[a.agent] || AGENTS.shell;
    const st = STATES[a.state] || STATES.running;
    const working = a.state === 'working', blocked = a.state === 'blocked';
    const pulse = working ? 1 + 0.07 * Math.sin(t * 6 + a.phase) : blocked ? 1 + 0.05 * Math.sin(t * 10) : 1 + 0.025 * Math.sin(t * 1.6 + a.phase);
    const r = a.r * pulse;
    let x = a.x, y = a.y;
    if (blocked && !calmBox.checked) x += Math.sin(t * 40) * 1.2;

    // glow
    const glowR = r * (working ? 3.0 : blocked ? 2.6 : 2.1);
    const glow = ctx.createRadialGradient(x, y, r * 0.3, x, y, glowR);
    glow.addColorStop(0, hexA(working ? ag.color : st.color, working ? 0.45 : 0.28));
    glow.addColorStop(1, hexA(ag.color, 0));
    ctx.fillStyle = glow; ctx.beginPath(); ctx.arc(x, y, glowR, 0, 6.29); ctx.fill();

    if (working) {
      // thinking ring: two arcs counter-rotating
      ctx.lineWidth = 2.4; ctx.lineCap = 'round';
      ctx.strokeStyle = hexA(ag.color, 0.9);
      ctx.beginPath(); ctx.arc(x, y, r + 8, t * 3.2 + a.phase, t * 3.2 + a.phase + 1.9); ctx.stroke();
      ctx.strokeStyle = hexA(st.color, 0.75);
      ctx.beginPath(); ctx.arc(x, y, r + 13, -t * 2.1, -t * 2.1 + 1.2); ctx.stroke();
      // orbiting sparks
      for (let i = 0; i < 3; i++) {
        const ang = t * (2.2 + i * 0.6) + i * 2.09 + a.phase, rr = r + 18 + i * 3;
        ctx.fillStyle = i === 1 ? st.color : ag.color;
        ctx.beginPath(); ctx.arc(x + Math.cos(ang) * rr, y + Math.sin(ang) * rr * 0.62, 2.4, 0, 6.29); ctx.fill();
      }
      // code sparks rising
      a.sparkT += dt;
      const every = calmBox.checked ? 0.45 : 0.16;
      while (a.sparkT > every) { a.sparkT -= every; spark(a, Math.random() < 0.5 ? ag.color : st.color); }
      // typing bars
      const bw = 4, gap = 3, n = 6, bx = x - (n * (bw + gap) - gap) / 2, by = y + r + 30;
      for (let i = 0; i < n; i++) {
        const h = 3 + 9 * Math.abs(Math.sin(t * 9 + i * 1.3 + a.phase));
        ctx.fillStyle = hexA(st.color, 0.85);
        ctx.fillRect(bx + i * (bw + gap), by - h, bw, h);
      }
    } else if (blocked) {
      for (let i = 0; i < 2; i++) {
        const p = (t * 1.2 + i * 0.5) % 1;
        ctx.strokeStyle = hexA(st.color, 0.8 * (1 - p)); ctx.lineWidth = 2;
        ctx.beginPath(); ctx.arc(x, y, r + 4 + p * 26, 0, 6.29); ctx.stroke();
      }
    } else if (a.state === 'done') {
      a.ringT += dt;
      const p = (a.ringT * 0.7) % 1;
      ctx.strokeStyle = hexA(st.color, 0.75 * (1 - p)); ctx.lineWidth = 2.2;
      ctx.beginPath(); ctx.arc(x, y, r + 4 + p * 30, 0, 6.29); ctx.stroke();
    }

    // core
    const core = ctx.createRadialGradient(x - r * 0.35, y - r * 0.4, r * 0.1, x, y, r);
    core.addColorStop(0, lighten(ag.color, 0.35)); core.addColorStop(1, darken(ag.color, a.state === 'idle' ? 0.55 : 0.25));
    ctx.fillStyle = core; ctx.beginPath(); ctx.arc(x, y, r, 0, 6.29); ctx.fill();
    ctx.lineWidth = 1.5; ctx.strokeStyle = hexA('#ffffff', a.state === 'idle' ? 0.15 : 0.35); ctx.stroke();
    ctx.fillStyle = '#ffffff'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.font = `700 ${Math.round(r * (ag.glyph.length > 1 ? 0.7 : 0.95))}px "Segoe UI Symbol", "Segoe UI", system-ui, sans-serif`;
    ctx.globalAlpha = a.state === 'idle' ? 0.6 : 1;
    ctx.fillText(ag.glyph, x, y + 1);
    ctx.globalAlpha = 1;

    // badge
    if (blocked || a.state === 'done' || a.state === 'done-seen') {
      const bx = x + r * 0.78, by = y - r * 0.78, br = Math.max(7, r * 0.34);
      ctx.fillStyle = st.color; ctx.beginPath(); ctx.arc(bx, by, br, 0, 6.29); ctx.fill();
      ctx.fillStyle = '#0d0f14'; ctx.font = `800 ${Math.round(br * 1.3)}px "Segoe UI", sans-serif`;
      ctx.fillText(blocked ? '!' : '✓', bx, by + (blocked ? 1 : 0.5));
    }

    // focused terminal: dashed ring in the sidebar's blue
    if (a.focused) {
      ctx.setLineDash([5, 5]); ctx.lineDashOffset = -t * 18;
      ctx.strokeStyle = '#3794ff'; ctx.lineWidth = 1.8;
      ctx.beginPath(); ctx.arc(x, y, r + (working ? 24 : 10), 0, 6.29); ctx.stroke();
      ctx.setLineDash([]);
    }

    // label
    const ly = y + r + (working ? 44 : 18);
    ctx.font = '600 12px "Segoe UI", system-ui, sans-serif';
    ctx.fillStyle = a.focused ? '#7fb8ff' : '#d8dee9';
    ctx.fillText(clip(a.title, 120), x, ly);
    ctx.font = '10px "Cascadia Mono", Consolas, monospace';
    ctx.fillStyle = st.color;
    ctx.fillText(st.label, x, ly + 14);
    a.hit = { x, y, r: Math.max(r + 10, 26), top: y - r - 14, bottom: ly + 20 };
  }

  function drawParticles(dt) {
    const step = dt * 60;
    particles = particles.filter(p => (p.life += step) < p.max);
    for (const p of particles) {
      p.vy += p.g * step; p.x += p.vx * step; p.y += p.vy * step;
      const a = 1 - p.life / p.max;
      ctx.globalAlpha = a;
      ctx.fillStyle = p.color;
      if (p.text) {
        ctx.font = `600 ${p.size | 0}px "Cascadia Mono", Consolas, monospace`;
        ctx.textAlign = 'center'; ctx.fillText(p.text, p.x, p.y);
      } else {
        ctx.beginPath(); ctx.arc(p.x, p.y, p.size * (0.5 + a * 0.5), 0, 6.29); ctx.fill();
      }
    }
    ctx.globalAlpha = 1;
    if (particles.length > 900) particles.splice(0, particles.length - 900);
  }

  // ── colour helpers ───────────────────────────────────────────────────────
  function rgb(hex) { const n = parseInt(hex.slice(1), 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255]; }
  function hexA(hex, a) { const [r, g, b] = rgb(hex); return `rgba(${r},${g},${b},${a})`; }
  function lighten(hex, k) { const c = rgb(hex).map(v => Math.round(v + (255 - v) * k)); return `rgb(${c})`; }
  function darken(hex, k) { const c = rgb(hex).map(v => Math.round(v * (1 - k))); return `rgb(${c})`; }

  // ── loop ─────────────────────────────────────────────────────────────────
  let last = now(), raf = 0;
  function frame() {
    raf = requestAnimationFrame(frame);
    const t = now(), dt = Math.min(0.05, t - last); last = t;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    background(t);
    ctx.save();
    if (shake > 0) { ctx.translate((Math.random() - 0.5) * shake, (Math.random() - 0.5) * shake); shake *= 0.86; if (shake < 0.3) shake = 0; }
    for (const z of zones) drawZone(z, t);
    for (const z of zones)
      for (const s of z.ws.sessions || []) { const a = avatars.get(keyOf(z.ws, s)); if (a) drawAvatar(a, t, dt); }
    drawParticles(dt);
    ctx.restore();
  }

  function resize() {
    const rect = stageEl.getBoundingClientRect();
    dpr = window.devicePixelRatio || 1;
    W = Math.max(1, rect.width); H = Math.max(1, rect.height);
    canvas.width = Math.round(W * dpr); canvas.height = Math.round(H * dpr);
    layout();
  }
  new ResizeObserver(resize).observe(stageEl);

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { cancelAnimationFrame(raf); raf = 0; }
    else if (!raf) { last = now(); frame(); }
  });

  // ── interaction ──────────────────────────────────────────────────────────
  function pick(ev) {
    const rect = canvas.getBoundingClientRect();
    const mx = ev.clientX - rect.left, my = ev.clientY - rect.top;
    for (const a of avatars.values()) {
      const h = a.hit; if (!h) continue;
      if (Math.hypot(mx - h.x, my - h.y) <= h.r || (Math.abs(mx - h.x) < 60 && my >= h.top && my <= h.bottom)) return a;
    }
    return null;
  }
  canvas.addEventListener('mousemove', (ev) => {
    hover = pick(ev);
    canvas.style.cursor = hover ? 'pointer' : 'default';
    if (!hover) { tipEl.hidden = true; return; }
    const ag = AGENTS[hover.agent] || AGENTS.shell, st = STATES[hover.state] || STATES.running;
    tipEl.innerHTML = '';
    const line = document.createElement('div'); line.textContent = `${hover.ws.name} / ${hover.title}`;
    const sub = document.createElement('small'); sub.textContent = `${ag.name} · ${st.label}${hover.focused ? ' · focused' : ''} — click to open`;
    tipEl.append(line, sub);
    tipEl.style.left = `${hover.hit.x}px`; tipEl.style.top = `${hover.hit.top}px`;
    tipEl.hidden = false;
  });
  canvas.addEventListener('mouseleave', () => { tipEl.hidden = true; hover = null; });
  canvas.addEventListener('click', async (ev) => {
    const a = pick(ev); if (!a) return;
    burst(a.hit.x, a.hit.y, ['#3794ff', '#ffffff'], 14, 2.2);
    if (DEMO) { demoFocus(a); return; }
    try { await window.zero.invoke('sessions.focus', { group: a.session.group, tab: a.session.tab }); }
    catch (e) { console.warn('sessions.focus', e); }
  });

  // ── data source ──────────────────────────────────────────────────────────
  async function poll() {
    if (!document.hidden) {
      try { apply(DEMO ? demoTick() : await window.zero.invoke('sessions.list')); }
      catch (e) {
        console.warn('sessions.list', e);
        apply({ supported: false, workspaces: [] });
      }
    }
    setTimeout(poll, POLL_MS);
  }

  // Simulated sessions for previewing the stage outside the app.
  let demo = null;
  function demoTick() {
    if (!demo) {
      demo = [
        { name: 'AgentZeroLite', path: '/demo/azl', tabs: [['Claude', 'claude'], ['Codex', 'codex'], ['bash', 'shell']] },
        { name: 'web-shop', path: '/demo/shop', tabs: [['Claude-2', 'claude'], ['AgentOne', 'agentone']] },
        { name: 'infra', path: '/demo/infra', tabs: [['Codex-2', 'codex']] },
        { name: 'research', path: '/demo/research', tabs: [['Netclaw', 'netclaw'], ['Claude-3', 'claude']] },
      ].map((w, g) => ({ group: g, name: w.name, path: w.path, active: g === 0,
        sessions: w.tabs.map(([title, agent], t) => ({ group: g, tab: t, title, agent, state: agent === 'shell' ? 'running' : 'idle', focused: g === 0 && t === 0, needsAttention: false })) }));
    }
    const pool = ['working', 'working', 'working', 'blocked', 'done', 'done-seen', 'idle'];
    for (const w of demo)
      for (const s of w.sessions)
        if (s.agent !== 'shell' && Math.random() < 0.18) {
          s.state = pool[(Math.random() * pool.length) | 0];
          s.needsAttention = s.state === 'blocked' || s.state === 'done';
        }
    return { supported: true, workspaces: JSON.parse(JSON.stringify(demo)) };
  }
  function demoFocus(a) {
    for (const w of demo) { w.active = w.group === a.session.group; for (const s of w.sessions) s.focused = w.active && s.tab === a.session.tab; }
    apply({ supported: true, workspaces: JSON.parse(JSON.stringify(demo)) });
  }

  resize();
  frame();
  poll();
})();
