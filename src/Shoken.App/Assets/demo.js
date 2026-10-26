// Demo de entonación: el mismo texto leído con la entonación de Shoken y sin ella. La app arma la página (los datos van
// en #demo-data) y genera «Tu voz»; aquí se reproduce, se dibujan las dos líneas de tiempo y las notas, y se resalta lo
// que suena con el mismo estilo que la lectura normal (window.lector.highlight).
(() => {
  'use strict';

  const data = JSON.parse(document.getElementById('demo-data').textContent);
  const segments = data.segments;
  const count = segments.length;
  const $ = (id) => document.getElementById(id);
  const post = (message) => {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(message);
    }
  };

  const NAMES = { con: 'Con Shoken', sin: 'Sin entonación' };
  const PLAY = 'M8 5v14l11-7z';
  const PAUSE = 'M6 5h4v14H6zM14 5h4v14h-4z';
  const audio = { con: $('demo-audio-con'), sin: $('demo-audio-sin') };
  const playButton = $('demo-play');
  const mineButton = $('demo-mine');
  const recordedButton = document.querySelector('[data-voice="recorded"]');
  const msg = $('demo-msg');
  const docEl = $('demo-doc');
  const notesAside = $('demo-notes');

  let voice = 'recorded';      // 'recorded' (Dalia, incluida en la app) o 'mine' (con el motor del usuario)
  let version = 'con';
  let takes = data.recorded;   // { con: { src, duration, marks }, sin: { … } } de la voz elegida
  let mine = data.mine;        // «Tu voz» si ya se generó
  let waitingMine = false;     // se eligió «Tu voz» y la app todavía no la entrega
  let speed = data.speed === 1.25 ? 1.25 : 1;
  let current = -1;
  let raf = 0;
  let lanes = null;

  const element = (tag, className) => {
    const el = document.createElement(tag);
    if (className) el.className = className;
    return el;
  };
  const number = (n) => n.toLocaleString('es-MX');
  const clock = (s) => {
    s = Math.max(0, Math.floor(s));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const seconds = (ms) => (ms / 1000).toFixed(2).replace(/0$/, '').replace(/\.$/, '');
  const signed = (n) => (n > 0 ? '+' : n < 0 ? '−' : '') + Math.abs(n) + ' %';
  const toneDetail = (c) => 'velocidad ' + signed(c.rate) + ', tono ' + signed(c.pitch);
  // "más lento y grave", "más lenta y aguda": cómo suena un cambio de velocidad y tono.
  function toneWords(c, feminine) {
    const words = [];
    if (c.rate) words.push(c.rate < 0 ? (feminine ? 'más lenta' : 'más lento') : (feminine ? 'más rápida' : 'más rápido'));
    if (c.pitch) words.push(c.pitch < 0 ? (words.length ? 'grave' : 'más grave') : (words.length ? (feminine ? 'aguda' : 'agudo') : (feminine ? 'más aguda' : 'más agudo')));
    return words.join(' y ');
  }
  const engineName = (label) => (label || '').split(' · ').pop();
  // "Dora · Kokoro" → "Dora (Kokoro)".
  const pretty = (label) => {
    const [name, engine] = (label || '').split(' · ');
    return engine ? name + ' (' + engine + ')' : name;
  };

  // ---- Notas: lo que hace Shoken en cada parte ----

  const elementOf = segments.map((s) => docEl.querySelector('[data-seg="' + CSS.escape(s.anchor) + '"]'));

  function chipsFor(s) {
    const chips = [];
    if (s.pause > 0) chips.push({ kind: 'pause', text: 'pausa ' + seconds(s.pause) + ' s', title: 'Silencio que pide Shoken antes de esta parte' });
    if (s.prosody) chips.push({ kind: 'tone', text: toneWords(s.prosody, false), title: (s.kind === 'Heading' ? 'Título: ' : '') + toneDetail(s.prosody) });
    if (s.strongProsody) {
      for (const phrase of s.strong) {
        chips.push({ kind: 'tone', text: 'negrita: ' + toneWords(s.strongProsody, true), title: '«' + phrase + '»: ' + toneDetail(s.strongProsody) });
      }
    }
    if (s.quotedProsody) {
      for (const phrase of s.quoted) {
        chips.push({ kind: 'tone', text: 'entre comillas: ' + toneWords(s.quotedProsody, false), title: phrase + ': ' + toneDetail(s.quotedProsody) });
      }
    }
    if (s.asideProsody) {
      for (const phrase of s.asides) {
        chips.push({ kind: 'tone', text: 'paréntesis: ' + toneWords(s.asideProsody, false), title: phrase + ': ' + toneDetail(s.asideProsody) });
      }
    }
    if (s.announces) chips.push({ kind: 'added', text: 'dice «' + s.announces + '»', title: 'Lo agrega Shoken: en el Markdown es una alerta' });
    if (s.number) chips.push({ kind: 'added', text: 'lee el número', title: 'Elemento de una lista numerada' });
    if (s.period) chips.push({ kind: 'added', text: '+ punto', title: 'Shoken agrega un punto final para que la voz cierre la frase y baje el tono' });
    return chips;
  }

  // Una nota por bloque del documento (varios segmentos pueden compartir uno).
  const notes = [];
  segments.forEach((s, i) => {
    const el = elementOf[i];
    if (!el) return;
    let note = notes.length && notes[notes.length - 1].el === el ? notes[notes.length - 1] : null;
    if (!note) {
      note = { el, segs: [], node: element('div', 'demo-note') };
      notes.push(note);
    }
    note.segs.push(i);
    for (const chip of chipsFor(s)) {
      const span = element('span', 'demo-chip ' + chip.kind);
      span.textContent = chip.text;
      span.title = chip.title;
      note.node.append(span);
    }
  });

  const compact = () => window.matchMedia('(max-width: 760px)').matches;

  // Con la ventana ancha, cada nota va en la columna de la derecha a la altura de su bloque (si se pisan, la de abajo
  // se corre); angosta, debajo de su bloque.
  function layoutNotes() {
    if (compact()) {
      for (const note of notes) {
        note.node.style.top = '';
        const title = note.el.querySelector(':scope > .markdown-alert-title'); // la alerta lleva adentro su texto
        (title || note.el).after(note.node);
      }
      return;
    }

    const base = notesAside.getBoundingClientRect().top;
    let bottom = -Infinity;
    for (const note of notes) {
      if (note.node.parentElement !== notesAside) notesAside.append(note.node);
      const top = Math.max(note.el.getBoundingClientRect().top - base + 3, bottom + 6);
      note.node.style.top = top + 'px';
      bottom = top + note.node.offsetHeight;
    }
    notesAside.style.minHeight = Math.max(0, bottom) + 'px';
  }

  // Con Kokoro o Piper no cambia el tono: esas notas se tachan.
  function updateToneNotes() {
    const off = voice === 'mine' && mine && mine.local;
    for (const chip of document.querySelectorAll('.demo-chip.tone')) {
      chip.classList.toggle('off', !!off);
      if (off) chip.dataset.title = chip.dataset.title || chip.title;
      chip.title = off ? 'Con ' + engineName(mine.label) + ' no cambia el tono' : chip.dataset.title || chip.title;
    }
  }

  // ---- Líneas de tiempo ----

  function buildLanes() {
    const box = $('demo-lanes');
    box.textContent = '';
    lanes = null;
    if (!takes) return;

    const scale = Math.max(takes.con.duration, takes.sin.duration);
    const pct = (t) => (Math.max(0, t) / scale * 100).toFixed(3) + '%';
    lanes = { pct };
    for (const v of ['con', 'sin']) {
      const take = takes[v];
      const lane = element('div', 'demo-lane' + (v === version ? ' active' : ''));
      lane.dataset.version = v;
      const name = element('div', 'demo-lane-name');
      const small = element('small');
      small.textContent = clock(take.duration) + ' · ' + take.duration.toFixed(1) + ' s';
      name.append(NAMES[v], small);
      const track = element('div', 'demo-track');
      track.title = 'Clic para escuchar ' + NAMES[v].toLowerCase() + ' desde ahí';
      const blocks = [];
      for (let i = 0; i < count; i++) {
        const start = take.marks[i];
        // En la versión con Shoken, la pausa que precede al segmento siguiente se dibuja aparte.
        const gap = v === 'con' && i < count - 1 ? segments[i + 1].pause / 1000 : 0;
        const end = i < count - 1 ? take.marks[i + 1] - gap : take.duration;
        const block = element('div', 'demo-blk' + (segments[i].kind === 'Heading' ? ' h' : ''));
        block.style.left = pct(start);
        block.style.width = 'calc(' + pct(Math.max(0.05, end - start)) + ' - 1px)';
        block.title = segments[i].text;
        track.append(block);
        blocks.push(block);
        if (v === 'con' && i > 0 && segments[i].pause > 0) {
          const pause = element('div', 'demo-pause');
          pause.style.left = pct(start - segments[i].pause / 1000);
          pause.style.width = pct(segments[i].pause / 1000);
          track.append(pause);
        }
      }
      const head = element('div', 'demo-head');
      track.append(head);
      track.addEventListener('click', (e) => {
        const rect = track.getBoundingClientRect();
        const t = (e.clientX - rect.left) / rect.width * scale;
        if (t <= take.duration) playFrom(v, segmentAt(v, t));
      });
      lane.append(name, track);
      box.append(lane);
      lanes[v] = { lane, head, blocks };

      if (v === 'con') {
        // Las pausas largas, con su duración debajo.
        const row = element('div', 'demo-axis-row');
        const labels = element('div', 'demo-labels');
        for (let i = 1; i < count; i++) {
          if (segments[i].pause >= 900) {
            const label = element('span');
            label.style.left = pct(take.marks[i] - segments[i].pause / 2000);
            label.textContent = seconds(segments[i].pause) + ' s';
            labels.append(label);
          }
        }
        row.append(element('span'), labels);
        box.append(row);
      }
    }

    const row = element('div', 'demo-axis-row');
    const axis = element('div', 'demo-axis');
    for (let s = 0; s <= scale; s += 10) {
      const tick = element('span');
      tick.style.left = pct(s);
      tick.textContent = s + ' s';
      axis.append(tick);
    }
    row.append(element('span'), axis);
    box.append(row);
    current = -2;
  }

  // ---- Reproducción ----

  const other = (v) => audio[v === 'con' ? 'sin' : 'con'];
  const isPlaying = () => !audio[version].paused && !audio[version].ended;
  const whenReady = (a) => a.readyState >= 1 ? Promise.resolve() : new Promise((resolve) => a.addEventListener('loadedmetadata', resolve, { once: true }));

  function segmentAt(v, t) {
    const marks = takes[v].marks;
    let i = 0;
    for (let k = 0; k < count; k++) {
      if (marks[k] <= t + 0.02) i = k;
    }
    return i;
  }

  async function seek(v, t) {
    const a = audio[v];
    await whenReady(a);
    a.currentTime = Math.min(Math.max(0, t) + 0.001, Math.max(0, takes[v].duration - 0.05));
  }

  function setVersion(v) {
    version = v;
    current = -2;
    for (const button of document.querySelectorAll('.demo-switch [data-version]')) {
      button.setAttribute('aria-pressed', String(button.dataset.version === v));
    }
    if (lanes) {
      for (const k of ['con', 'sin']) lanes[k].lane.classList.toggle('active', k === v);
    }
  }

  async function playFrom(v, i) {
    if (!takes) return;
    if (v !== version) setVersion(v);
    other(v).pause();
    await seek(v, takes[v].marks[i]);
    audio[v].play().catch(() => {});
    render();
  }

  // Cambiar de versión sigue en el mismo segmento, sonando si sonaba: así se compara parte por parte.
  async function switchVersion(v) {
    if (v === version || !takes) return;
    const from = audio[version];
    const playing = isPlaying();
    const started = playing || from.currentTime > 0.01;
    const i = segmentAt(version, from.currentTime);
    from.pause();
    setVersion(v);
    if (started) await seek(v, takes[v].marks[i]);
    if (playing) audio[v].play().catch(() => {});
    render();
  }

  function toggle() {
    if (!takes) return;
    const a = audio[version];
    if (a.paused || a.ended) {
      if (a.ended) a.currentTime = 0;
      other(version).pause();
      a.play().catch(() => {});
    } else {
      a.pause();
    }
  }

  function stop() {
    for (const a of Object.values(audio)) a.pause();
    if (takes && audio[version].readyState >= 1) audio[version].currentTime = 0;
    render();
  }

  async function step(delta) {
    if (!takes) return;
    const i = Math.min(count - 1, Math.max(0, (current < 0 ? (delta > 0 ? -1 : 0) : current) + delta));
    if (isPlaying()) {
      playFrom(version, i);
    } else {
      await seek(version, takes[version].marks[i]);
      render();
    }
  }

  function pause() {
    for (const a of Object.values(audio)) a.pause();
  }

  function applySpeed() {
    for (const a of Object.values(audio)) {
      a.defaultPlaybackRate = speed;
      a.playbackRate = speed;
    }
    for (const button of document.querySelectorAll('[data-speed]')) {
      button.setAttribute('aria-pressed', String(Number(button.dataset.speed) === speed));
    }
  }

  // Otra voz (la grabada o la del usuario): mismas versión y parte, sonando si sonaba.
  async function useTakes(newVoice, newTakes) {
    const a = audio[version];
    const playing = isPlaying();
    const started = playing || a.currentTime > 0.01;
    const i = takes && started ? segmentAt(version, a.currentTime) : 0;
    pause();
    voice = newVoice;
    takes = newTakes;
    audio.con.src = takes.con.src;
    audio.sin.src = takes.sin.src;
    applySpeed();
    buildLanes();
    updateVoiceButtons();
    updateToneNotes();
    if (started) await seek(version, takes[version].marks[i]);
    if (playing) audio[version].play().catch(() => {});
    render();
  }

  function markSegment(i) {
    if (i === current) return;
    current = i;
    if (window.lector) window.lector.highlight(i >= 0 ? segments[i].anchor : null, isPlaying());
    for (const note of notes) note.node.classList.toggle('now', i >= 0 && note.segs.includes(i));
    if (lanes) {
      for (const v of ['con', 'sin']) lanes[v].blocks.forEach((block, k) => block.classList.toggle('now', v === version && k === i));
    }
  }

  function render() {
    const playing = takes ? isPlaying() : false;
    $('demo-play-icon').setAttribute('d', playing ? PAUSE : PLAY);
    $('demo-play-text').textContent = playing ? 'Pausa' : 'Reproducir';
    playButton.setAttribute('aria-label', playing ? 'Pausa' : 'Reproducir');
    playButton.disabled = !takes;
    if (!takes) return;
    const t = audio[version].currentTime;
    if (lanes) lanes[version].head.style.left = lanes.pct(t);
    $('demo-clock').textContent = clock(t) + ' / ' + clock(takes[version].duration);
    markSegment(playing || t > 0 ? segmentAt(version, t) : -1); // en 0 exacto, todavía no empezó
  }

  function loop() {
    render();
    raf = isPlaying() ? requestAnimationFrame(loop) : 0;
  }

  for (const v of ['con', 'sin']) {
    audio[v].addEventListener('play', () => {
      if (v !== version) return;
      cancelAnimationFrame(raf);
      raf = requestAnimationFrame(loop);
    });
    for (const type of ['pause', 'ended', 'seeked', 'loadedmetadata']) {
      audio[v].addEventListener(type, () => { if (v === version) render(); });
    }
  }

  // ---- «Tu voz»: la genera la app con el motor del usuario ----

  function showMessage(text, actions = [], progress = null) {
    msg.textContent = '';
    const span = element('span');
    span.textContent = text;
    msg.append(span);
    if (progress !== null) {
      const bar = element('div', 'demo-progress');
      const fill = element('i');
      fill.style.width = Math.round(progress * 100) + '%';
      bar.append(fill);
      msg.append(bar);
    }
    for (const action of actions) {
      const button = element('button', action.primary ? 'primary' : '');
      button.type = 'button';
      button.textContent = action.text;
      button.addEventListener('click', action.run);
      msg.append(button);
    }
    msg.hidden = false;
  }

  const hideMessage = () => { msg.hidden = true; msg.textContent = ''; };

  function cancelMine() {
    waitingMine = false;
    post({ type: 'demo', action: 'cancel' });
    hideMessage();
    updateVoiceButtons();
  }

  function updateVoiceButtons() {
    recordedButton.setAttribute('aria-pressed', String(voice === 'recorded'));
    mineButton.setAttribute('aria-pressed', String(voice === 'mine'));
    recordedButton.disabled = !data.recorded;
    const label = mine ? mine.label : data.engine.label;
    mineButton.textContent = label ? 'Tu voz: ' + pretty(label) : 'Tu voz';
    mineButton.disabled = !data.engine.available && !mine;
    mineButton.title = mineButton.disabled ? data.engine.reason || '' : 'El mismo texto con el motor y la voz que tienes configurados';
  }

  function showLocalNote() {
    if (voice === 'mine' && mine && mine.local) {
      const name = engineName(mine.label);
      showMessage('Con ' + name + ' se oyen las pausas y el punto final, pero no cambia el tono de títulos y negritas: eso solo lo hacen Azure y Google.',
        [{ text: 'Entendido', run: hideMessage }]);
    } else {
      hideMessage();
    }
  }

  function chooseMine() {
    if (voice === 'mine' || waitingMine) return;
    if (mine) {
      useTakes('mine', mine).then(showLocalNote);
      return;
    }
    waitingMine = true;
    showMessage('Preparando tu voz…');
    post({ type: 'demo', action: 'voice' });
  }

  function chooseRecorded() {
    if (waitingMine) cancelMine();
    if (voice !== 'recorded' && data.recorded) {
      useTakes('recorded', data.recorded);
      hideMessage();
    }
  }

  window.shokenDemo = {
    toggle,
    stop,
    step,
    pause,
    voiceConfirm({ provider, characters, remaining }) {
      showMessage('Generar tu voz con ' + provider + ' gasta unos ' + number(characters) + ' caracteres de tu cuota del mes (te quedan ' +
        number(remaining) + '). Lo que se genere queda en caché: la próxima vez no gasta.', [
        { text: 'Generar', primary: true, run: () => { showMessage('Generando…'); post({ type: 'demo', action: 'generate' }); } },
        { text: 'Cancelar', run: () => { waitingMine = false; hideMessage(); } },
      ]);
    },
    voiceProgress({ done, total, label }) {
      showMessage('Generando con ' + pretty(label) + ': ' + done + ' de ' + total + ' partes…', [{ text: 'Cancelar', run: cancelMine }], done / total);
    },
    voiceReady(ready) {
      mine = ready;
      updateVoiceButtons();
      if (!waitingMine) return;
      waitingMine = false;
      useTakes('mine', ready).then(showLocalNote);
    },
    voiceFailed({ message, retry }) {
      waitingMine = false;
      const close = { text: 'Cerrar', run: hideMessage };
      showMessage(message, retry ? [{ text: 'Reintentar', primary: true, run: chooseMine }, close] : [close]);
      updateVoiceButtons();
    },
    voiceCanceled() {
      waitingMine = false;
      hideMessage();
      updateVoiceButtons();
    },
  };

  // ---- Controles ----

  playButton.addEventListener('click', toggle);
  for (const button of document.querySelectorAll('.demo-switch [data-version]')) {
    button.addEventListener('click', () => switchVersion(button.dataset.version));
  }
  for (const button of document.querySelectorAll('[data-speed]')) {
    button.addEventListener('click', () => { speed = Number(button.dataset.speed); applySpeed(); });
  }
  recordedButton.addEventListener('click', chooseRecorded);
  mineButton.addEventListener('click', chooseMine);

  // Clic en una parte del documento: se escucha desde ahí en la versión elegida.
  docEl.addEventListener('click', (e) => {
    const selection = window.getSelection();
    if (selection && !selection.isCollapsed) return;
    const block = e.target instanceof Element ? e.target.closest('[data-seg]') : null;
    const i = block ? segments.findIndex((s) => s.anchor === block.getAttribute('data-seg')) : -1;
    if (i >= 0) playFrom(version, i);
  });

  // Espacio, Esc y las flechas manejan la demo. Se atienden antes que lector.js para que no lleguen a la lectura normal;
  // los demás atajos (pestañas, abrir, configuración) siguen su camino.
  window.addEventListener('keydown', (e) => {
    if (e.ctrlKey || e.altKey || e.metaKey || ![' ', 'Escape', 'ArrowLeft', 'ArrowRight'].includes(e.key)) return;
    const target = e.target instanceof Element ? e.target : null;
    if (target && target.closest('input, textarea, .lector-find')) return;
    e.stopPropagation();
    if (e.key === ' ' && target && target.closest('button')) return; // Espacio sobre un botón lo pulsa
    e.preventDefault();
    if (e.repeat) return;
    if (e.key === ' ') toggle();
    else if (e.key === 'Escape') stop();
    else step(e.key === 'ArrowRight' ? 1 : -1);
  }, true);

  // La pestaña dejó de verse (ventana minimizada u oculta): la demo no sigue sonando.
  document.addEventListener('visibilitychange', () => { if (document.hidden) pause(); });

  // ---- Arranque ----

  if (data.cost) {
    const pct = Math.round(data.cost.intonation / Math.max(1, data.cost.text) * 100);
    $('demo-cost').textContent = 'La entonación también se paga: Azure cobra el SSML completo, no solo el texto, y en esta demo las pausas y los ' +
      'cambios de tono suman ' + number(data.cost.intonation) + ' caracteres, un ' + pct + ' % sobre el texto. Lo que ya sonó queda en caché: ' +
      'escucharlo otra vez no gasta.';
  }

  if (takes) {
    audio.con.src = takes.con.src;
    audio.sin.src = takes.sin.src;
  } else if (mine) {
    voice = 'mine';
    takes = mine;
    audio.con.src = takes.con.src;
    audio.sin.src = takes.sin.src;
  } else {
    showMessage('Esta compilación no trae la grabación de la demo. Elige «Tu voz» para generarla con tu motor.');
  }

  applySpeed();
  buildLanes();
  updateVoiceButtons();
  updateToneNotes();
  layoutNotes();
  render();
  new ResizeObserver(layoutNotes).observe(docEl);
  if (document.fonts) document.fonts.ready.then(layoutNotes);
})();
