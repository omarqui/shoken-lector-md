// Puente entre el documento renderizado y la app: resaltado, doble clic, enlaces y atajos.
(() => {
  'use strict';

  // https://app.lector/lector.js: de aquí salen los demás recursos de la app (mermaid.min.js).
  const scriptBase = (document.currentScript && document.currentScript.src) || location.href;

  const post = (message) => {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(message);
    }
  };

  let highlighted = [];
  let follow = true;
  let followTimer = 0;

  // El data-seg de un bloque de código está en <code>; se resalta el <pre> completo.
  const visualTarget = (el) =>
    el.tagName === 'CODE' && el.parentElement && el.parentElement.tagName === 'PRE' ? el.parentElement : el;

  const elementsFor = (anchor) =>
    Array.from(document.querySelectorAll('[data-seg="' + CSS.escape(anchor) + '"]')).map(visualTarget);

  function highlight(anchor, scroll = true) {
    for (const el of highlighted) {
      el.classList.remove('lector-reading');
    }
    highlighted = anchor ? elementsFor(anchor) : [];
    for (const el of highlighted) {
      el.classList.add('lector-reading');
    }

    const first = highlighted[0];
    if (first && follow && scroll) {
      const rect = first.getBoundingClientRect();
      const height = window.innerHeight || document.documentElement.clientHeight;
      if (rect.top < height * 0.15 || rect.bottom > height * 0.8) {
        first.scrollIntoView({ block: 'center', behavior: 'smooth' });
      }
    }
  }

  function scrollToAnchor(anchor) {
    const first = elementsFor(anchor)[0];
    if (first) {
      first.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }
  }

  function scrollToHeading(anchor) {
    const first = elementsFor(anchor)[0];
    if (first) {
      first.scrollIntoView({ block: 'start', behavior: 'smooth' });
    }
  }

  function scrollToY(y) {
    window.scrollTo(0, y);
    diagrams.rememberScroll(y);
  }

  // La app guarda la posición de cada pestaña para restaurarla al volver o al reabrir.
  let scrollTimer = 0;
  window.addEventListener('scroll', () => {
    clearTimeout(scrollTimer);
    scrollTimer = setTimeout(() => post({ type: 'scroll', y: Math.round(window.scrollY) }), 250);
  }, { passive: true });

  // Si el usuario se desplaza a mano, se deja de seguir la lectura unos segundos.
  const pauseFollow = () => {
    follow = false;
    clearTimeout(followTimer);
    followTimer = setTimeout(() => { follow = true; }, 5000);
  };
  window.addEventListener('wheel', pauseFollow, { passive: true });
  window.addEventListener('touchmove', pauseFollow, { passive: true });

  document.addEventListener('dblclick', (e) => {
    const el = e.target instanceof Element ? e.target.closest('[data-seg]') : null;
    if (!el) {
      return;
    }
    const selection = window.getSelection();
    if (selection) {
      selection.removeAllRanges();
    }
    follow = true;
    post({ type: 'playFrom', anchor: el.getAttribute('data-seg') });
  });

  document.addEventListener('click', (e) => {
    if (!(e.target instanceof Element)) {
      return;
    }

    const command = e.target.closest('[data-command]');
    if (command) {
      e.preventDefault();
      post({ type: 'command', command: command.getAttribute('data-command') });
      return;
    }

    const link = e.target.closest('a[href]');
    if (!link) {
      return;
    }

    e.preventDefault();
    const href = link.getAttribute('href') || '';
    if (href.startsWith('#')) {
      let id = href.slice(1);
      try { id = decodeURIComponent(id); } catch { /* %-escape mal formado: se usa tal cual */ }
      const target = document.getElementById(id);
      if (target) {
        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
      return;
    }
    post({ type: 'link', href: link.href });
  });

  // Índice del texto del documento, compartido por la búsqueda y los apuntes. Es una copia "plegada" (sin acentos
  // ni mayúsculas, con los espacios seguidos como uno) que recuerda de qué nodo y posición salió cada carácter, así
  // un texto se encuentra aunque cambie el formato y se resalta con la API de resaltado de CSS, sin tocar el DOM.
  const textIndex = (() => {
    const fold = (s) => s.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();
    let cached = null;

    function build() {
      const root = document.querySelector('main.doc') || document.body;
      const nodes = [], segs = [], lines = [], nodeOf = [], offsetOf = [];
      let text = '';
      let space = true;
      let lastSeg = null;
      let lastLine = 0;
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
        acceptNode: (n) => (n.parentElement && n.parentElement.closest('script, style')
          ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT),
      });
      for (let n = walker.nextNode(); n; n = walker.nextNode()) {
        const block = n.parentElement.closest('[data-seg]');
        if (block) { // fuera de un bloque legible: el anterior
          lastSeg = block.getAttribute('data-seg');
          lastLine = Number(block.getAttribute('data-line')) || lastLine;
        }
        const k = nodes.push(n) - 1;
        segs.push(lastSeg);
        lines.push(lastLine);
        const s = n.nodeValue;
        for (let i = 0; i < s.length; i++) {
          const c = s.charCodeAt(i);
          const f = c <= 32 || (c > 127 && /\s/.test(s[i])) ? ' ' : (c < 128 ? s[i].toLowerCase() : fold(s[i]));
          if (f === ' ') {
            if (space) continue; // los espacios seguidos (y los saltos de línea del HTML) cuentan como uno
            space = true;
          } else if (f) {
            space = false;
          }
          for (let j = 0; j < f.length; j++) {
            text += f[j];
            nodeOf.push(k);
            offsetOf.push(i);
          }
        }
      }
      return { text, nodes, segs, lines, nodeOf, offsetOf };
    }

    // Cada aparición de "query" (ya plegada) como un rango, con el bloque y la línea donde empieza y termina.
    function findAll(query, limit) {
      const { text, nodes, segs, lines, nodeOf, offsetOf } = get();
      const found = [];
      for (let at = query ? text.indexOf(query) : -1; at >= 0 && found.length < limit; at = text.indexOf(query, at + query.length)) {
        const end = at + query.length - 1;
        const range = document.createRange();
        range.setStart(nodes[nodeOf[at]], offsetOf[at]);
        range.setEnd(nodes[nodeOf[end]], offsetOf[end] + 1);
        found.push({ range, seg: segs[nodeOf[at]], endSeg: segs[nodeOf[end]], line: lines[nodeOf[at]] });
      }
      return found;
    }

    // El texto no cambia mientras la página está abierta, salvo al dibujar los diagramas.
    const get = () => cached || (cached = build());

    return { fold, findAll, invalidate: () => { cached = null; } };
  })();

  // Búsqueda (Ctrl+F): sin distinguir acentos ni mayúsculas, sobre el índice compartido. La app recibe cuántos
  // resultados hay en cada bloque para mostrarlos en el índice.
  const find = (() => {
    const MAX_MATCHES = 5000;
    const fold = textIndex.fold;
    let bar = null;
    let input = null;
    let count = null;
    let playButton = null;
    let matches = []; // { range, seg }
    let current = -1;
    let timer = 0;

    function ensureBar() {
      if (bar) {
        return;
      }
      bar = document.createElement('div');
      bar.className = 'lector-find';
      bar.setAttribute('role', 'search');
      bar.hidden = true;
      bar.innerHTML =
        '<span class="lector-find-icon" aria-hidden="true">&#xE721;</span>' +
        '<input type="text" placeholder="Buscar en el documento" aria-label="Buscar en el documento" spellcheck="false" autocomplete="off">' +
        '<span class="lector-find-count" aria-live="polite"></span>' +
        '<button type="button" data-find="previous" title="Anterior (Shift+Enter)" aria-label="Resultado anterior">&#xE70E;</button>' +
        '<button type="button" data-find="next" title="Siguiente (Enter)" aria-label="Resultado siguiente">&#xE70D;</button>' +
        '<button type="button" data-find="play" title="Leer desde aquí (Ctrl+Enter)" aria-label="Leer desde este resultado">&#xE768;</button>' +
        '<button type="button" data-find="close" title="Cerrar (Esc)" aria-label="Cerrar la búsqueda">&#xE711;</button>';
      document.body.appendChild(bar);
      input = bar.querySelector('input');
      count = bar.querySelector('.lector-find-count');
      playButton = bar.querySelector('[data-find="play"]');
      input.addEventListener('input', () => {
        clearTimeout(timer);
        timer = setTimeout(search, 120);
      });
      bar.addEventListener('click', (e) => {
        const button = e.target instanceof Element ? e.target.closest('[data-find]') : null;
        if (!button) {
          return;
        }
        const action = button.getAttribute('data-find');
        if (action === 'close') close();
        else if (action === 'play') play();
        else go(action === 'next' ? 1 : -1);
      });
      // Que la selección dentro de la barra no abra el botón de pronunciación.
      bar.addEventListener('mouseup', (e) => e.stopPropagation());
    }

    function search() {
      clearTimeout(timer);
      timer = 0;
      const query = fold(input.value).replace(/\s+/g, ' ').trim();
      matches = [];
      current = -1;
      if (query) {
        matches = textIndex.findAll(query, MAX_MATCHES);
        // Se empieza por el primer resultado visible (o el siguiente), no por el principio del documento.
        current = matches.length ? Math.max(0, matches.findIndex((m) => m.range.getBoundingClientRect().bottom >= 0)) : -1;
      }
      paint();
      reveal();
      report(query.length > 0);
    }

    function paint() {
      bar.classList.toggle('lector-find-none', input.value.trim() !== '' && matches.length === 0);
      count.textContent = matches.length
        ? (current + 1) + ' de ' + matches.length + (matches.length >= MAX_MATCHES ? '+' : '')
        : (input.value.trim() ? 'Sin resultados' : '');
      playButton.disabled = !(current >= 0 && matches[current].seg);
      if (!(window.CSS && CSS.highlights)) {
        return;
      }
      CSS.highlights.set('lector-find', new Highlight(...matches.map((m) => m.range)));
      const active = new Highlight(...(current >= 0 ? [matches[current].range] : []));
      active.priority = 1;
      CSS.highlights.set('lector-find-current', active);
    }

    function reveal() {
      if (current < 0) {
        return;
      }
      const rect = matches[current].range.getBoundingClientRect();
      const height = window.innerHeight || document.documentElement.clientHeight;
      if (rect.top < height * 0.15 || rect.bottom > height * 0.85) {
        pauseFollow(); // que el resaltado de la lectura no se lleve la vista mientras se busca
        window.scrollBy({ top: rect.top - height * 0.4, behavior: 'smooth' });
      }
    }

    function report(active) {
      const counts = {};
      for (const m of matches) {
        if (m.seg) counts[m.seg] = (counts[m.seg] || 0) + 1;
      }
      post({ type: 'find', active, counts });
    }

    function go(step) {
      if (timer) {
        search(); // Enter antes de que termine la espera de la escritura: se queda en el primer resultado
        return;
      }
      if (!matches.length) {
        return;
      }
      current = (current + step + matches.length) % matches.length;
      paint();
      reveal();
    }

    function play() {
      if (timer) {
        search();
      }
      const seg = current >= 0 ? matches[current].seg : null;
      if (seg) {
        follow = true;
        post({ type: 'playFrom', anchor: seg });
      }
    }

    function open() {
      if (!document.querySelector('[data-seg]')) {
        return; // bienvenida o pestaña vacía: no hay nada que buscar
      }
      ensureBar();
      const reopened = bar.hidden && input.value.trim() !== '';
      bar.hidden = false;
      input.focus();
      input.select();
      if (reopened) {
        search(); // vuelve con la búsqueda anterior
      } else {
        paint(); // contador y ▶ según lo que haya (al abrir por primera vez, nada)
      }
    }

    function close() {
      if (!bar || bar.hidden) {
        return;
      }
      clearTimeout(timer);
      timer = 0;
      bar.hidden = true;
      matches = [];
      current = -1;
      paint();
      report(false);
      input.blur(); // el foco queda en el documento: los atajos vuelven a funcionar
    }

    // true si la tecla ya se atendió aquí y no debe llegar a los atajos del lector.
    function handleKey(e) {
      if (e.ctrlKey && !e.altKey && (e.key === 'f' || e.key === 'F')) {
        e.preventDefault();
        open();
        return true;
      }
      const isOpen = bar && !bar.hidden;
      if (e.key === 'F3' && isOpen) {
        e.preventDefault();
        go(e.shiftKey ? -1 : 1);
        return true;
      }
      if (!(isOpen && e.target instanceof Element && bar.contains(e.target))) {
        return false;
      }
      if (e.key === 'Enter' && e.target === input) {
        e.preventDefault();
        if (e.ctrlKey) play();
        else go(e.shiftKey ? -1 : 1);
      } else if (e.key === 'Escape') {
        e.preventDefault();
        close();
      }
      return true; // dentro de la barra las teclas escriben o pulsan sus botones: ningún atajo del lector
    }

    // El texto de la página cambió (se dibujaron los diagramas): se vuelve a buscar sobre el índice nuevo.
    function refresh() {
      if (bar && !bar.hidden && input.value.trim()) {
        search();
      }
    }

    return { open, close, handleKey, refresh };
  })();

  document.addEventListener('keydown', (e) => {
    if (find.handleKey(e)) {
      return;
    }
    if (e.ctrlKey && e.altKey && (e.key === 'm' || e.key === 'M')) {
      e.preventDefault();
      notes.createHere(); // como en Word y Google Docs
      return;
    }
    if (e.key === 'Escape' && document.querySelector('.lector-menu')) {
      notes.closeMenu();
      return;
    }
    if (e.target instanceof Element && e.target.closest('.lector-note-ui')) {
      return; // escribiendo un apunte: ningún atajo del lector
    }
    if (e.repeat) {
      if (e.key === ' ') e.preventDefault(); // mantener presionado Espacio no alterna una y otra vez
      return;
    }
    let command = null;
    if (e.key === ' ' && !e.ctrlKey && !e.altKey && !e.metaKey) command = 'toggle';
    else if (e.key === 'Escape') command = 'stop';
    else if (e.ctrlKey && e.key === 'ArrowRight') command = 'next';
    else if (e.ctrlKey && e.key === 'ArrowLeft') command = 'previous';
    else if (e.ctrlKey && (e.key === 'o' || e.key === 'O')) command = 'open';
    else if (e.ctrlKey && e.key === ',') command = 'settings';
    else if (e.key === 'F5') command = 'reload';
    else if (e.ctrlKey && (e.key === 'v' || e.key === 'V')) command = 'paste';
    else if (e.ctrlKey && (e.key === 't' || e.key === 'T')) command = 'newTab';
    else if (e.ctrlKey && (e.key === 'w' || e.key === 'W' || e.key === 'F4')) command = 'closeTab';
    else if (e.ctrlKey && e.key === 'Tab') command = e.shiftKey ? 'previousTab' : 'nextTab';
    else if (e.ctrlKey && (e.key === 'b' || e.key === 'B')) command = 'outline';
    else if (e.key === 'ArrowLeft' && !e.ctrlKey && !e.altKey && !e.shiftKey) command = 'back10';
    else if (e.key === 'ArrowRight' && !e.ctrlKey && !e.altKey && !e.shiftKey) command = 'forward10';

    if (command) {
      e.preventDefault();
      post({ type: 'command', command });
    }
  });

  // Selección en el documento: la app muestra un chip para agregar un apunte y, si son 1 a 3 palabras ("short"),
  // para editar su pronunciación. Se espera un instante porque el doble clic (leer desde aquí) también selecciona
  // una palabra y luego la quita.
  let selectionTimer = 0;
  let selectionShown = false;
  const blockOf = (node) => {
    const el = node && (node.nodeType === 1 ? node : node.parentElement);
    return el ? el.closest('[data-seg]') : null;
  };
  // La selección usable para un apunte (dentro del documento), o null.
  function documentSelection() {
    const selection = window.getSelection();
    const text = selection ? selection.toString().trim() : '';
    const doc = document.querySelector('main.doc');
    if (!selection || selection.rangeCount === 0 || !text || text.length > 3000 || !doc) return null;
    const range = selection.getRangeAt(0);
    if (!doc.contains(range.commonAncestorContainer)) return null;
    return { range, text };
  }
  document.addEventListener('mouseup', (e) => {
    if (e.target instanceof Element && e.target.closest('.lector-note-ui')) return; // dentro de los apuntes
    clearTimeout(selectionTimer);
    selectionTimer = setTimeout(() => {
      const current = documentSelection();
      if (!current) {
        if (selectionShown) {
          selectionShown = false;
          post({ type: 'selectionCleared' });
        }
        return;
      }
      const { range, text } = current;
      const words = text.split(/\s+/).filter(Boolean);
      const rect = range.getBoundingClientRect();
      const block = blockOf(range.startContainer);
      notes.rememberSelection(range);
      selectionShown = true;
      post({
        type: 'selection',
        text: text.replace(/^[^\p{L}\p{N}#.]+|[^\p{L}\p{N}#+]+$/gu, ''),
        short: text.length <= 40 && words.length <= 3 && !/\n/.test(text),
        x: rect.left, y: rect.bottom,
        anchor: block ? block.getAttribute('data-seg') : null,
      });
    }, 350);
  });
  // Al desplazar, el botón quedaría en otro lugar: se esconde (solo si estaba visible, para no inundar mensajes).
  window.addEventListener('scroll', () => {
    if (selectionShown) {
      selectionShown = false;
      post({ type: 'selectionCleared' });
    }
  }, { passive: true });

  // Diagramas Mermaid (```mermaid): Markdig deja el código en <pre class="mermaid" data-seg>. Mermaid pesa 5 MB y
  // se carga solo si el documento tiene alguno. Se dibuja con los colores de lector.css y otra vez al cambiar de tema.
  const diagrams = (() => {
    const blocks = Array.from(document.querySelectorAll('pre.mermaid')).map((el) => ({ el, source: el.textContent }));
    const dark = window.matchMedia('(prefers-color-scheme: dark)');
    let loading = null;
    let generation = 0;
    let counter = 0;
    let pending = blocks.length > 0;
    let restoreY = null;

    // La app restaura la posición guardada (en píxeles, medida con los diagramas ya dibujados) apenas carga la página.
    // Si llega antes de que terminen de dibujarse, se vuelve a aplicar al final, salvo que el usuario ya se haya movido.
    function rememberScroll(y) {
      if (pending) restoreY = y;
    }
    for (const type of ['wheel', 'keydown', 'mousedown', 'touchstart']) {
      window.addEventListener(type, () => { restoreY = null; }, { capture: true, passive: true });
    }

    function finishFirstDraw() {
      if (!pending) return;
      pending = false;
      if (restoreY !== null) {
        window.scrollTo(0, restoreY);
        restoreY = null;
      }
    }

    function load() {
      loading = loading || new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = new URL('mermaid.min.js', scriptBase).href; // la página tiene <base> en la carpeta del documento
        script.onload = () => (window.mermaid ? resolve(window.mermaid) : reject(new Error('Mermaid no se inicializó.')));
        script.onerror = () => reject(new Error('No se pudo cargar mermaid.min.js.'));
        document.head.appendChild(script);
      });
      return loading;
    }

    function themeVariables() {
      const css = getComputedStyle(document.documentElement);
      const v = (name) => css.getPropertyValue(name).trim();
      return {
        darkMode: dark.matches,
        background: v('--bg'),
        primaryColor: v('--soft'),
        primaryTextColor: v('--fg'),
        primaryBorderColor: v('--accent'),
        secondaryColor: v('--reading'),
        tertiaryColor: v('--bg'),
        lineColor: v('--muted'),
        textColor: v('--fg'),
        fontFamily: '"Segoe UI Variable Text", "Segoe UI", sans-serif',
        fontSize: '15px',
      };
    }

    // Cambiar la altura de un bloque no debe mover lo que el usuario está viendo (ni la posición restaurada al abrir).
    function keepingPosition(change) {
      const anchor = Array.from(document.querySelectorAll('[data-seg]')).find((el) => el.getBoundingClientRect().bottom > 0);
      const before = anchor ? anchor.getBoundingClientRect().top : 0;
      change();
      if (anchor) {
        const delta = anchor.getBoundingClientRect().top - before;
        if (Math.abs(delta) > 1) window.scrollBy(0, delta);
      }
    }

    async function render() {
      const mine = ++generation;
      let mermaid;
      try {
        mermaid = await load();
      } catch (e) {
        blocks.forEach((b) => keepingPosition(() => fail(b, e)));
        finishFirstDraw();
        return;
      }
      mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'base', themeVariables: themeVariables() });
      for (const block of blocks) {
        const id = 'lector-diagrama-' + (++counter);
        try {
          const { svg } = await mermaid.render(id, block.source);
          if (mine !== generation) return; // otro cambio de tema ya está dibujando
          keepingPosition(() => {
            block.el.innerHTML = svg;
            block.el.classList.remove('lector-diagram-pending', 'lector-diagram-error');
            block.el.classList.add('lector-diagram');
            block.el.removeAttribute('title');
          });
        } catch (e) {
          // Si el código tiene un error, Mermaid deja su borrador en la página.
          for (const leftover of [document.getElementById(id), document.getElementById('d' + id)]) {
            if (leftover && !blocks.some((b) => b.el === leftover)) leftover.remove();
          }
          if (mine !== generation) return;
          keepingPosition(() => fail(block, e));
        }
      }
      finishFirstDraw();
      textChanged(); // el texto de los diagramas cambió: búsqueda y apuntes se ubican de nuevo
    }

    // Se muestra el código, con el motivo al pasar el mouse.
    function fail(block, error) {
      block.el.textContent = block.source;
      block.el.classList.remove('lector-diagram-pending', 'lector-diagram');
      block.el.classList.add('lector-diagram-error');
      block.el.title = 'No se pudo dibujar el diagrama: ' + ((error && error.message) || error);
    }

    if (blocks.length) {
      blocks.forEach((b) => b.el.classList.add('lector-diagram-pending'));
      render();
      dark.addEventListener('change', render);
    }

    return { count: blocks.length, rememberScroll, redraw: () => { if (blocks.length) render(); } };
  })();

  // Colores del tema elegido en la app (variables de lector.css); los diagramas se dibujan de nuevo con ellos.
  function setColors(colors) {
    for (const [name, value] of Object.entries(colors)) {
      document.documentElement.style.setProperty('--' + name, value);
    }
    diagrams.redraw();
  }

  // Apuntes: comentarios sobre el texto, para pasárselos a una IA. La app es la dueña (los guarda, sabe en qué
  // líneas del Markdown están y arma la exportación); aquí se ubican las citas, se subrayan y se editan las tarjetas.
  const notes = (() => {
    const KINDS = { Clarify: 'Aclarar', Improve: 'Mejorar', Other: 'Otro' };
    const COLUMN = 280;
    const MIN_DOC = 560; // con menos ancho para el texto, la columna se vuelve marcas en el margen
    let list = []; // { id, quote, kind, text, resolved, line, range, seg, endSeg, shownLine }
    let visible = true;
    let compact = false;
    let aside = null;
    let markers = null; // las marcas (en dots) y la tarjeta flotante (popover), con la ventana angosta
    let dots = null;
    let popover = null;
    let menu = null;
    let undoBar = null;
    let undoTimer = 0;
    let lastSelection = null;
    let activeId = null;
    let editingId = null;
    let newId = null;
    let menuBlock = null;
    let queued = false;

    const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const byId = (id) => list.find((n) => n.id === id);
    const make = (tag, className) => Object.assign(document.createElement(tag), { className });

    function rememberSelection(range) {
      lastSelection = range.cloneRange();
    }

    // La cita en la página: si aparece varias veces, la más cercana a la línea donde estaba.
    function locate(note) {
      const query = textIndex.fold(note.quote).replace(/\s+/g, ' ').trim();
      const found = textIndex.findAll(query, 200);
      if (!found.length) return null;
      return found.reduce((best, f) => (Math.abs(f.line - note.line) < Math.abs(best.line - note.line) ? f : best));
    }

    function anchorAll(report) {
      for (const n of list) {
        const where = locate(n);
        n.range = where ? where.range : null;
        n.seg = where ? where.seg : null;
        n.endSeg = where ? where.endSeg : null;
        n.shownLine = where ? where.line : n.line;
      }
      if (report && list.length) {
        post({ type: 'notesAnchored', results: list.map((n) => ({ id: n.id, found: !!n.range, startSeg: n.seg, endSeg: n.endSeg })) });
      }
    }

    // Lo que manda la app al abrir el documento o tras crear, borrar o restaurar; editId abre esa tarjeta para escribir.
    function set(items, show, editId) {
      // Si se estaba escribiendo otro apunte (y se creó uno nuevo), lo escrito se guarda antes de redibujar.
      const editor = editingId ? editorFor(editingId) : null;
      const draft = editor && editingId !== editId ? { id: editingId, text: editor.value.trim() } : null;
      if (popover) popover.remove();
      popover = null;
      visible = !!show;
      list = items.map((n) => ({ ...n }));
      const drafted = draft && byId(draft.id);
      if (drafted && drafted.text !== draft.text) {
        drafted.text = draft.text;
        post({ type: 'noteUpdate', id: draft.id, text: draft.text });
      }
      anchorAll(true);
      editingId = editId || null;
      newId = editId || null;
      render();
      if (editId) focusEditor(editId);
    }

    function setVisible(show) {
      visible = !!show;
      closePopover();
      render();
    }

    // El texto de la página cambió (se dibujaron los diagramas): las citas se vuelven a ubicar.
    function refresh() {
      if (!list.length) return;
      anchorAll(false);
      render();
    }

    function position(n) {
      return n.range ? n.range.getBoundingClientRect().top + window.scrollY : Infinity;
    }

    function ensureUi() {
      if (aside) return;
      aside = make('aside', 'lector-notes lector-note-ui');
      aside.setAttribute('aria-label', 'Apuntes');
      aside.innerHTML =
        '<div class="notes-head"><strong class="notes-title"></strong>' +
        '<button type="button" data-act="export" title="Copia los apuntes pendientes, con sus referencias, para pegarlos en el chat de una IA">Copiar para la IA</button>' +
        '<button type="button" data-act="clear">Borrar resueltos</button></div>' +
        '<div class="notes-cards"></div>';
      markers = make('div', 'lector-note-markers lector-note-ui');
      dots = make('div', 'note-marker-list');
      markers.appendChild(dots);
      document.body.append(aside, markers);
      for (const host of [aside, markers]) {
        host.addEventListener('click', onClick);
        // Elegir el tipo mientras se escribe no le quita el foco al cuadro de la nota.
        host.addEventListener('mousedown', (e) => {
          if (e.target instanceof Element && e.target.closest('[data-act="kind"]')) e.preventDefault();
        });
        host.addEventListener('change', onChange);
        host.addEventListener('keydown', onKey);
        host.addEventListener('focusout', onFocusOut);
        host.addEventListener('mouseover', (e) => activate(cardId(e.target)));
        host.addEventListener('mouseleave', () => activate(null));
      }
      const doc = document.querySelector('main.doc');
      if (doc && window.ResizeObserver) new ResizeObserver(queueLayout).observe(doc);
      window.addEventListener('resize', queueLayout);
    }

    function cardId(target) {
      const card = target instanceof Element ? target.closest('[data-id]') : null;
      return card ? card.getAttribute('data-id') : null;
    }

    // Arriba, los tres tipos siempre a la vista (el elegido, con su color) y la línea; abajo, leer, resuelto y borrar.
    function cardHtml(n) {
      const kind = n.kind in KINDS ? n.kind : 'Clarify';
      const kinds = Object.entries(KINDS).map(([value, name]) =>
        '<button type="button" role="radio" class="note-kind kind-' + value.toLowerCase() + '" data-act="kind" data-kind="' + value +
        '" aria-checked="' + (value === kind) + '">' + name + '</button>').join('');
      const where = n.range ? 'L' + n.shownLine : 'ya no está';
      const body = editingId === n.id
        ? '<textarea class="note-editor" rows="3" aria-label="Nota" placeholder="¿Qué quieres que la IA aclare o mejore?">' + esc(n.text) + '</textarea>' +
          '<div class="note-hint">Ctrl+Enter guarda · Esc cancela</div>'
        : '<div class="note-text" data-act="edit" title="Clic para editar">' + (n.text ? esc(n.text) : '<span class="note-empty">Escribe la nota…</span>') + '</div>';
      return '<article class="note-card kind-' + kind.toLowerCase() + (n.resolved ? ' resolved' : '') + (n.range ? '' : ' orphan') +
        (activeId === n.id ? ' active' : '') + '" data-id="' + esc(n.id) + '">' +
        '<div class="note-top"><div class="note-kinds" role="radiogroup" aria-label="Tipo de apunte">' + kinds + '</div>' +
        '<span class="note-where" title="' + (n.range ? 'Línea en el Markdown' : 'La cita ya no está en el documento') + '">' + where + '</span></div>' +
        '<blockquote class="note-quote">' + esc(n.quote) + '</blockquote>' + body +
        '<div class="note-actions">' +
        '<button type="button" class="note-icon" data-act="play" title="Leer desde aquí" aria-label="Leer desde aquí"' + (n.seg ? '' : ' disabled') + '>&#xE768;</button>' +
        '<label class="note-done"><input type="checkbox" data-act="resolved"' + (n.resolved ? ' checked' : '') + '> Resuelto</label>' +
        '<button type="button" class="note-icon" data-act="delete" title="Borrar" aria-label="Borrar apunte">&#xE74D;</button></div></article>';
    }

    function render() {
      ensureUi();
      const shown = visible && list.length > 0;
      document.body.classList.toggle('has-notes', shown);
      aside.hidden = !shown;
      markers.hidden = !shown;
      const pending = list.filter((n) => !n.resolved).length;
      aside.querySelector('.notes-title').textContent = 'Apuntes (' + pending + (list.length > pending ? ' de ' + list.length : '') + ')';
      aside.querySelector('[data-act="export"]').disabled = pending === 0;
      aside.querySelector('[data-act="clear"]').disabled = !list.some((n) => n.resolved);
      const ordered = [...list].sort((a, b) => position(a) - position(b) || a.line - b.line);
      aside.querySelector('.notes-cards').innerHTML = ordered.map(cardHtml).join('');
      if (popover && !byId(popover.getAttribute('data-for'))) closePopover();
      paint();
      layout();
    }

    function paint() {
      if (!(window.CSS && CSS.highlights)) return;
      const groups = { Clarify: [], Improve: [], Other: [] };
      for (const n of list) {
        if (visible && n.range && !n.resolved) (groups[n.kind] || groups.Clarify).push(n.range);
      }
      CSS.highlights.set('lector-note-clarify', new Highlight(...groups.Clarify));
      CSS.highlights.set('lector-note-improve', new Highlight(...groups.Improve));
      CSS.highlights.set('lector-note-other', new Highlight(...groups.Other));
      const active = visible && activeId ? byId(activeId) : null;
      const highlight = new Highlight(...(active && active.range ? [active.range] : []));
      highlight.priority = 2;
      CSS.highlights.set('lector-note-active', highlight);
    }

    function activate(id) {
      if (id === activeId) return;
      activeId = id;
      for (const card of document.querySelectorAll('.note-card')) card.classList.toggle('active', card.getAttribute('data-id') === id);
      paint();
    }

    function queueLayout() {
      if (queued) return;
      queued = true;
      requestAnimationFrame(() => {
        queued = false;
        layout();
      });
    }

    // Cada tarjeta a la altura de su cita; si se pisan, la de abajo se corre. Sin ancho, marcas en el margen.
    function layout() {
      if (!aside || aside.hidden) return;
      const doc = document.querySelector('main.doc');
      if (!doc) return;
      compact = window.innerWidth < MIN_DOC + COLUMN + 64;
      document.body.classList.toggle('notes-compact', compact);
      const docRect = doc.getBoundingClientRect();
      const height = document.documentElement.scrollHeight;
      if (compact) {
        layoutMarkers(docRect);
        return;
      }
      dots.innerHTML = '';
      closePopover();
      aside.style.left = (docRect.right + window.scrollX - 24) + 'px';
      aside.style.height = height + 'px';
      const head = aside.querySelector('.notes-head');
      let bottom = head.offsetTop + head.offsetHeight + 8;
      for (const card of aside.querySelectorAll('.note-card')) {
        const n = byId(card.getAttribute('data-id'));
        const wanted = n && n.range ? n.range.getBoundingClientRect().top + window.scrollY : bottom;
        const top = Math.max(wanted, bottom);
        card.style.top = top + 'px';
        bottom = top + card.offsetHeight + 10;
      }
      aside.style.height = Math.max(height, bottom) + 'px';
    }

    function layoutMarkers(docRect) {
      aside.style.left = '';
      // Solo se redibujan las marcas: la tarjeta flotante sigue en su lugar (y su cuadro de texto, con el foco).
      dots.innerHTML = list.filter((n) => n.range).map((n) =>
        '<button type="button" class="note-marker kind-' + (n.kind in KINDS ? n.kind : 'Clarify').toLowerCase() + (n.resolved ? ' resolved' : '') +
        '" data-act="open" data-id="' + esc(n.id) + '" title="' + esc(KINDS[n.kind] || 'Apunte') + ': ' + esc((n.text || n.quote).slice(0, 80)) +
        '" aria-label="Ver apunte" style="top:' + (n.range.getBoundingClientRect().top + window.scrollY) + 'px;left:' +
        (docRect.right + window.scrollX - 34) + 'px">&#xE70B;</button>').join('');
    }

    function openPopover(id) {
      const n = byId(id);
      if (!n || !n.range) return;
      closePopover();
      popover = make('div', 'note-popover');
      popover.setAttribute('data-for', id);
      popover.innerHTML = cardHtml(n);
      const rect = n.range.getBoundingClientRect();
      const width = Math.min(320, window.innerWidth - 32);
      popover.style.width = width + 'px';
      popover.style.top = (rect.bottom + window.scrollY + 6) + 'px';
      popover.style.left = Math.max(16, Math.min(rect.left, window.innerWidth - width - 16)) + window.scrollX + 'px';
      markers.appendChild(popover);
    }

    // Cerrar la tarjeta flotante (clic afuera, Esc, otra marca) guarda lo que se estaba escribiendo en ella.
    function closePopover() {
      if (!popover) return;
      const id = popover.getAttribute('data-for');
      const editor = popover.querySelector('.note-editor');
      popover.remove();
      popover = null;
      if (editor && editingId === id) commit(id, editor.value);
    }

    // La tarjeta está en la columna y, con la ventana angosta, también flotando: se cambian las dos.
    function rerenderCard(id) {
      const n = byId(id);
      if (!n) return;
      if (popover && popover.getAttribute('data-for') === id) popover.innerHTML = cardHtml(n);
      const card = aside.querySelector('.note-card[data-id="' + CSS.escape(id) + '"]');
      if (card) card.outerHTML = cardHtml(n);
      layout();
    }

    // El cuadro de texto que se ve: el de la tarjeta flotante si está abierta para ese apunte.
    function editorFor(id) {
      const host = popover && popover.getAttribute('data-for') === id ? popover : aside;
      return host ? host.querySelector('.note-card[data-id="' + CSS.escape(id) + '"] .note-editor') : null;
    }

    function focusEditor(id) {
      if (compact && !(popover && popover.getAttribute('data-for') === id)) openPopover(id);
      const editor = editorFor(id);
      if (!editor) return;
      editor.focus();
      editor.setSelectionRange(editor.value.length, editor.value.length);
      editor.closest('.note-card').scrollIntoView({ block: 'nearest' });
    }

    function startEditing(id) {
      if (editingId && editingId !== id) save(editingId);
      editingId = id;
      rerenderCard(id);
      focusEditor(id);
    }

    function save(id) {
      const editor = editorFor(id);
      if (editor) commit(id, editor.value);
    }

    function commit(id, value) {
      const n = byId(id);
      if (!n) return;
      editingId = null;
      if (newId === id && !value.trim()) {
        newId = null;
        remove(id, true); // recién creado y se dejó vacío: como en Google Docs, no queda
        return;
      }
      newId = null;
      n.text = value.trim();
      post({ type: 'noteUpdate', id, text: n.text });
      rerenderCard(id);
    }

    function cancel(id) {
      const n = byId(id);
      if (!n) return;
      editingId = null;
      if (newId === id && !n.text) {
        newId = null;
        remove(id, true); // un apunte recién creado y vacío no deja rastro
        return;
      }
      newId = null;
      rerenderCard(id);
    }

    function remove(id, silent) {
      list = list.filter((n) => n.id !== id);
      if (editingId === id) editingId = null;
      post({ type: 'noteDelete', id, silent: !!silent });
      if (popover && popover.getAttribute('data-for') === id) closePopover();
      render();
      if (!silent) showUndo(id);
    }

    function showUndo(id) {
      if (!undoBar) {
        undoBar = make('div', 'lector-undo lector-note-ui');
        undoBar.setAttribute('role', 'status');
        document.body.appendChild(undoBar);
        undoBar.addEventListener('click', (e) => {
          const button = e.target instanceof Element ? e.target.closest('[data-undo]') : null;
          if (!button) return;
          post({ type: 'noteRestore', id: button.getAttribute('data-undo') });
          undoBar.hidden = true;
        });
      }
      undoBar.innerHTML = 'Apunte borrado <button type="button" data-undo="' + esc(id) + '">Deshacer</button>';
      undoBar.hidden = false;
      clearTimeout(undoTimer);
      undoTimer = setTimeout(() => { undoBar.hidden = true; }, 6000);
    }

    function onClick(e) {
      const target = e.target instanceof Element ? e.target.closest('[data-act]') : null;
      if (!target) return;
      const act = target.getAttribute('data-act');
      const id = cardId(target);
      if (act === 'export') post({ type: 'notesExport' });
      else if (act === 'clear') post({ type: 'notesClearResolved' });
      else if (act === 'open') openPopover(target.getAttribute('data-id'));
      else if (act === 'kind' && id) setKind(id, target.getAttribute('data-kind'));
      else if (act === 'edit' && id) startEditing(id);
      else if (act === 'delete' && id) remove(id, false);
      else if (act === 'play' && id && byId(id) && byId(id).seg) {
        follow = true;
        post({ type: 'playFrom', anchor: byId(id).seg });
      }
    }

    // Cambiar el tipo no redibuja la tarjeta: si se está escribiendo la nota, el texto y el foco siguen ahí.
    function setKind(id, kind) {
      const n = byId(id);
      if (!n || !(kind in KINDS) || n.kind === kind) return;
      n.kind = kind;
      post({ type: 'noteUpdate', id, kind });
      for (const card of document.querySelectorAll('.note-card[data-id="' + CSS.escape(id) + '"]')) {
        card.classList.remove('kind-clarify', 'kind-improve', 'kind-other');
        card.classList.add('kind-' + kind.toLowerCase());
        for (const option of card.querySelectorAll('[data-kind]')) option.setAttribute('aria-checked', String(option.getAttribute('data-kind') === kind));
      }
      paint();
      if (compact) layout(); // el color de la marca en el margen
    }

    function onChange(e) {
      const id = cardId(e.target);
      const n = id ? byId(id) : null;
      if (!n) return;
      if (e.target.matches('[data-act="resolved"]')) {
        n.resolved = e.target.checked;
        post({ type: 'noteUpdate', id, resolved: n.resolved });
        render();
      }
    }

    function onKey(e) {
      if (!e.target.matches || !e.target.matches('.note-editor')) {
        if (e.key === 'Escape' && popover) closePopover();
        return;
      }
      const id = cardId(e.target);
      if (e.key === 'Enter' && e.ctrlKey) {
        e.preventDefault();
        save(id);
      } else if (e.key === 'Escape') {
        e.preventDefault();
        cancel(id);
      }
    }

    function onFocusOut(e) {
      // Al salir del cuadro (sin Esc), la nota se guarda.
      if (e.target.matches && e.target.matches('.note-editor') && editingId === cardId(e.target)) {
        setTimeout(() => {
          if (editingId && document.activeElement && !document.activeElement.matches('.note-editor')) save(editingId);
        }, 0);
      }
    }

    // Crear: con la selección (o la última que hubo, si el clic fue en el chip de la app).
    function createFromSelection() {
      const current = documentSelection()
        || (lastSelection && !lastSelection.collapsed ? { range: lastSelection, text: lastSelection.toString().trim() } : null);
      if (!current || !current.text) return false;
      const start = blockOf(current.range.startContainer);
      const end = blockOf(current.range.endContainer) || start;
      if (!start) return false;
      post({ type: 'noteCreate', quote: current.text, startSeg: start.getAttribute('data-seg'), endSeg: end.getAttribute('data-seg') });
      const selection = window.getSelection();
      if (selection) selection.removeAllRanges();
      lastSelection = null;
      return true;
    }

    // Un párrafo entero (sin selección: el que se está leyendo, o el del clic derecho).
    function createForSegment(anchor) {
      const el = elementsFor(anchor)[0];
      const text = el ? el.textContent.replace(/\s+/g, ' ').trim() : '';
      if (text) post({ type: 'noteCreate', quote: text, startSeg: anchor, endSeg: anchor });
    }

    // Ctrl+Alt+M: la selección; sin selección decide la app (el párrafo que se está leyendo).
    function createHere() {
      if (!createFromSelection()) post({ type: 'noteNoSelection' });
    }

    // Clic derecho en el documento: agregar un apunte (a la selección o al párrafo) y copiar.
    function openMenu(x, y, target) {
      closeMenu();
      const selection = documentSelection();
      menuBlock = selection ? null : blockOf(target);
      const items = [];
      if (selection) items.push(['note', 'Agregar apunte a la selección'], ['copy', 'Copiar']);
      else if (menuBlock) items.push(['note', 'Agregar apunte a este párrafo']);
      if (!items.length) return;
      menu = make('div', 'lector-menu lector-note-ui');
      menu.setAttribute('role', 'menu');
      menu.innerHTML = items.map(([act, label]) => '<button type="button" role="menuitem" data-menu="' + act + '">' + label + '</button>').join('');
      document.body.appendChild(menu);
      menu.style.left = Math.min(x, window.innerWidth - menu.offsetWidth - 8) + 'px';
      menu.style.top = Math.min(y, window.innerHeight - menu.offsetHeight - 8) + 'px';
      menu.addEventListener('click', (e) => {
        const button = e.target instanceof Element ? e.target.closest('[data-menu]') : null;
        if (!button) return;
        const act = button.getAttribute('data-menu');
        const block = menuBlock;
        closeMenu();
        if (act === 'copy') document.execCommand('copy');
        else if (selection) createFromSelection();
        else if (block) createForSegment(block.getAttribute('data-seg'));
      });
      menu.querySelector('button').focus();
    }

    function closeMenu() {
      if (menu) menu.remove();
      menu = null;
    }

    document.addEventListener('contextmenu', (e) => {
      if (e.target instanceof Element && e.target.closest('input, textarea, .lector-find')) return; // ahí, el menú normal
      e.preventDefault();
      openMenu(e.clientX, e.clientY, e.target);
    });
    document.addEventListener('mousedown', (e) => {
      if (menu && !(e.target instanceof Element && e.target.closest('.lector-menu'))) closeMenu();
      if (popover && !(e.target instanceof Element && e.target.closest('.note-popover, .note-marker'))) closePopover();
    }, true);
    window.addEventListener('scroll', closeMenu, { passive: true });
    window.addEventListener('blur', closeMenu);

    // Clic sobre un texto anotado: se marca su tarjeta (o se abre, con la ventana angosta).
    document.addEventListener('click', (e) => {
      if (!visible || !list.length || (e.target instanceof Element && e.target.closest('.lector-note-ui'))) return;
      const selection = window.getSelection();
      if (selection && !selection.isCollapsed) return;
      const point = document.caretRangeFromPoint ? document.caretRangeFromPoint(e.clientX, e.clientY) : null;
      if (!point) return;
      const hit = list.find((n) => n.range && !n.resolved && n.range.isPointInRange(point.startContainer, point.startOffset));
      if (!hit) return;
      activate(hit.id);
      if (compact) {
        openPopover(hit.id);
      } else {
        const card = aside.querySelector('.note-card[data-id="' + CSS.escape(hit.id) + '"]');
        if (card) {
          card.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
          card.classList.remove('flash');
          void card.offsetWidth;
          card.classList.add('flash');
        }
      }
    });

    return { set, setVisible, refresh, rememberSelection, createFromSelection, createForSegment, createHere, closeMenu };
  })();

  // Cambió el texto de la página (se dibujaron los diagramas): índice, búsqueda y apuntes al día.
  function textChanged() {
    textIndex.invalidate();
    find.refresh();
    notes.refresh();
  }

  window.lector = { highlight, scrollToAnchor, scrollToHeading, scrollToY, find, diagrams, setColors, notes };
})();
