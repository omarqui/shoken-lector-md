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
    if (anchor !== word.anchor()) {
      word.clear(); // la palabra marcada es del bloque anterior
    }
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

  // El primer bloque que se ve (data-seg) y cuántos píxeles de él quedaron arriba del borde; la app lo usa para
  // dejar la vista donde estaba cuando el documento cambia.
  function visibleBlock() {
    for (const el of document.querySelectorAll('[data-seg]')) {
      const first = elementsFor(el.getAttribute('data-seg'))[0] || el;
      const rect = first.getBoundingClientRect();
      if (rect.bottom > 1) {
        return { anchor: el.getAttribute('data-seg'), offset: -rect.top };
      }
    }
    return null;
  }

  function scrollToBlock(anchor, offset) {
    const first = elementsFor(anchor)[0];
    if (first) {
      scrollToY(first.getBoundingClientRect().top + window.scrollY + (offset || 0));
    }
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
      const nodes = [], segs = [], lines = [], nodeOf = [], offsetOf = [], blocks = [];
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
        const from = text.length;
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
        // Cada bloque legible (data-seg) es un tramo seguido del texto plegado.
        if (lastSeg !== null && text.length > from) {
          const last = blocks[blocks.length - 1];
          if (last && last.seg === lastSeg) last.end = text.length;
          else blocks.push({ seg: lastSeg, line: lastLine, start: from, end: text.length });
        }
      }
      return { text, nodes, segs, lines, nodeOf, offsetOf, blocks };
    }

    // El rango de la página que corresponde a [start, end) del texto plegado.
    function rangeOf(start, end) {
      const { nodes, nodeOf, offsetOf } = get();
      const range = document.createRange();
      range.setStart(nodes[nodeOf[start]], offsetOf[start]);
      range.setEnd(nodes[nodeOf[end - 1]], offsetOf[end - 1] + 1);
      return range;
    }

    // Cada aparición de "query" (ya plegada) como un rango, con el bloque y la línea donde empieza y termina.
    function findAll(query, limit) {
      const { text, segs, lines, nodeOf } = get();
      const found = [];
      for (let at = query ? text.indexOf(query) : -1; at >= 0 && found.length < limit; at = text.indexOf(query, at + query.length)) {
        const end = at + query.length;
        found.push({ range: rangeOf(at, end), seg: segs[nodeOf[at]], endSeg: segs[nodeOf[end - 1]], line: lines[nodeOf[at]] });
      }
      return found;
    }

    // El texto no cambia mientras la página está abierta, salvo al dibujar los diagramas.
    const get = () => cached || (cached = build());

    return {
      fold,
      findAll,
      rangeOf,
      blocks: () => get().blocks, // [{ seg, line, start, end }], en offsets del texto plegado
      slice: (start, end) => get().text.slice(start, end),
      invalidate: () => { cached = null; },
    };
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
    if (e.defaultPrevented) {
      return; // ya la atendió un apunte (Esc cancela y redibuja la tarjeta: el destino ya no está en la página)
    }
    if (find.handleKey(e)) {
      return;
    }
    if (e.ctrlKey && e.altKey && (e.key === 'm' || e.key === 'M')) {
      e.preventDefault();
      notes.createHere(); // como en Word y Google Docs
      return;
    }
    if (e.altKey && !e.ctrlKey && !e.shiftKey && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
      e.preventDefault();
      notes.go(e.key === 'ArrowDown' ? 1 : -1);
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
    // Marcar sin parar: una marca por pulsación (mantenerla apretada no repite) y nunca escribiendo (ni en un apunte).
    else if ((e.key === 'm' || e.key === 'M') && !e.repeat && !e.ctrlKey && !e.altKey && !e.shiftKey && !e.metaKey
      && !(e.target instanceof Element && e.target.closest('input, textarea, [contenteditable], .lector-note-ui'))) command = 'mark';

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
    const NEAR_LINES = 40; // cuántas líneas alrededor de la guardada cuentan como "cerca" de donde estaba la cita
    const MIN_LIKENESS = 0.6; // parecido mínimo para seguir a una cita retocada; menos, y queda huérfana
    const MAX_SPAN_WORDS = 400; // con citas más largas solo se comparan bloques enteros (los tramos costarían mucho)
    const MAX_LCS_CELLS = 20e6; // tope de LCS por apunte para los tramos que cruzan bloques (unas decenas de ms)
    let list = []; // { id, quote, kind, text, resolved, line, range, seg, endSeg, shownLine, current }
    let undoAction = null; // lo que hace «Deshacer» en el aviso que se ve
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
    let pinnedId = null; // la elegida (navegada, tocada o en edición): su tarjeta va justo a la altura de su cita
    let hoverId = null; // la que está bajo el mouse: se despliega y corre hacia abajo a las que pisa
    const baseTops = new Map(); // dónde quedó cada tarjeta en el último layout(), sin la del mouse desplegada
    let link = null; // la línea de la cita a su tarjeta
    let editingId = null;
    let reportedEditing = false; // lo último que se le dijo a la app sobre editingId
    let newId = null;
    let menuBlock = null;
    let queued = false;

    const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const byId = (id) => list.find((n) => n.id === id);
    const make = (tag, className) => Object.assign(document.createElement(tag), { className });

    function rememberSelection(range) {
      lastSelection = range.cloneRange();
    }

    // La app no recarga la página mientras hay un apunte abierto para escribir (se perdería lo escrito): se le avisa al
    // abrirlo y al cerrarlo. Se avisa al terminar el cambio, así pasar de una tarjeta a otra no cuenta como cerrar.
    function reportEditing() {
      queueMicrotask(() => {
        const editing = editingId !== null;
        if (editing === reportedEditing) return;
        reportedEditing = editing;
        post({ type: 'noteEditing', editing });
      });
    }

    // La cita en la página, en este orden: tal cual cerca de la línea donde estaba; si no, lo más parecido cerca (se
    // retocó: current dice cómo quedó); si no, tal cual en cualquier parte (se movió). Entre varias, la más cercana.
    function locate(note) {
      const query = textIndex.fold(note.quote).replace(/\s+/g, ' ').trim();
      const found = textIndex.findAll(query, 200);
      const distance = (where) => Math.abs(where.line - note.line);
      const nearest = (places) => places.reduce((best, f) => (distance(f) < distance(best) ? f : best));
      const near = found.filter((f) => distance(f) <= NEAR_LINES);
      if (near.length) return nearest(near);
      return similar(query, note.line) || (found.length ? nearest(found) : null);
    }

    const wordsOf = (folded) => Array.from(folded.matchAll(/[\p{L}\p{N}]+/gu), (m) => ({ word: m[0], start: m.index, end: m.index + m[0].length }));

    // Lo más parecido a la cita cerca de su línea, si llega al parecido mínimo. Parecido = 2·LCS / (|a| + |b|), con
    // las palabras plegadas como unidad. Las palabras de los bloques cercanos se toman seguidas, en el orden del
    // documento: así también sigue a una cita de varios párrafos o elementos de lista que se retocó.
    function similar(query, line) {
      const quote = query.split(/[^\p{L}\p{N}]+/u).filter(Boolean);
      if (!quote.length) return null;
      const all = textIndex.blocks();
      const isNear = (block) => Math.abs(block.line - line) <= NEAR_LINES;
      const first = all.findIndex(isNear);
      if (first < 0) return null;
      const near = all.slice(first, all.findLastIndex(isNear) + 1);
      const words = []; // { word, start, end, block }: posiciones en el texto plegado; block, el índice en near
      const firstWord = near.map(() => -1), lastWord = near.map(() => -1); // las palabras de cada bloque
      near.forEach((block, b) => {
        for (const w of wordsOf(textIndex.slice(block.start, block.end))) {
          if (firstWord[b] < 0) firstWord[b] = words.length;
          lastWord[b] = words.length;
          words.push({ word: w.word, start: block.start + w.start, end: block.start + w.end, block: b });
        }
      });
      const plain = words.map((w) => w.word);
      const best = likeliest(quote, plain, words, near.map((block) => (isNear(block) ? Math.abs(block.line - line) : null)), firstWord, lastWord);
      if (!best) return null;
      // La proyección no sale de los bloques donde empieza y termina el tramo.
      const lo = firstWord[words[best.from].block], hi = lastWord[words[best.to].block];
      const [from, to] = best.from > lo || best.to < hi ? project(quote, plain, best.from, best.to, lo, hi) : [best.from, best.to];
      // Con la puntuación pegada a las palabras de los extremos (comillas, el punto final), como se selecciona.
      const startBlock = near[words[from].block], endBlock = near[words[to].block];
      const text = textIndex.slice(startBlock.start, endBlock.end);
      let start = words[from].start - startBlock.start;
      let end = words[to].end - startBlock.start;
      while (start > 0 && !/[\s\p{L}\p{N}]/u.test(text[start - 1])) start--;
      while (end < text.length && !/[\s\p{L}\p{N}]/u.test(text[end])) end++;
      const range = textIndex.rangeOf(startBlock.start + start, startBlock.start + end);
      return { range, seg: startBlock.seg, endSeg: endBlock.seg, line: startBlock.line, current: range.toString().replace(/\s+/g, ' ').trim() };
    }

    // Los candidatos, en el orden del documento: cada bloque cercano entero y los tramos de ceil(0,7·n) a ceil(1,3·n)
    // palabras que empiezan en él, aunque sigan en los bloques siguientes. Gana el mayor parecido; en un empate, el
    // que empieza más cerca de la línea. Si esos tramos costarían más que MAX_LCS_CELLS, o la cita pasa de 400 palabras,
    // se compara cada bloque por su lado (bestSpan). distances[b] es null si el bloque b no está cerca.
    function likeliest(quote, plain, words, distances, firstWord, lastWord) {
      const n = quote.length;
      const minLen = Math.ceil(0.7 * n), maxLen = Math.ceil(1.3 * n);
      let starts = n <= MAX_SPAN_WORDS ? windowStarts(quote, plain, minLen, maxLen, (s) => distances[words[s].block] !== null) : null;
      if (starts && starts.length * n * maxLen > MAX_LCS_CELLS) starts = null;
      let best = null;
      const consider = (score, from, to, distance) => {
        if (score >= MIN_LIKENESS && (!best || score > best.score || (score === best.score && distance < best.distance))) {
          best = { score, from, to, distance };
        }
      };
      let next = 0; // el próximo de starts
      for (let b = 0; b < distances.length; b++) {
        const lo = firstWord[b], hi = lastWord[b], distance = distances[b];
        if (lo < 0 || distance === null) continue;
        const block = plain.slice(lo, hi + 1);
        if (!starts) {
          const span = bestSpan(quote, block);
          if (span) consider(span.score, lo + span.from, lo + span.to, distance);
          continue;
        }
        consider(wholeScore(quote, block), lo, hi, distance);
        for (; next < starts.length && starts[next] <= hi; next++) {
          const s = starts[next];
          const lcs = lcsFrom(quote, plain, s, maxLen);
          for (let len = minLen; len < lcs.length; len++) consider(2 * lcs[len] / (n + len), s, s + len - 1, distance);
        }
      }
      return best;
    }

    // Dónde puede empezar un tramo que llegue al mínimo: en una palabra de la cita (el que empieza con otra nunca gana
    // al que empieza en la siguiente; salvo el último inicio posible) y con bastantes palabras de la cita en las
    // maxLen siguientes (el LCS no pasa de las que tienen en común). Se cuentan con una ventana que se corre.
    function windowStarts(quote, words, minLen, maxLen, allowed) {
      const n = quote.length;
      const need = new Map();
      for (const w of quote) need.set(w, (need.get(w) || 0) + 1);
      const count = new Map();
      let shared = 0, end = 0; // en común entre la cita y words[s..end)
      const starts = [];
      for (let s = 0; s + minLen <= words.length; s++) {
        for (const limit = Math.min(words.length, s + maxLen); end < limit; end++) {
          const k = count.get(words[end]) || 0;
          if (k < (need.get(words[end]) || 0)) shared++;
          count.set(words[end], k + 1);
        }
        if (allowed(s) && (need.has(words[s]) || s + minLen === words.length) && 2 * shared / (n + minLen) >= MIN_LIKENESS) {
          starts.push(s);
        }
        const k = count.get(words[s]);
        count.set(words[s], k - 1);
        if (k <= (need.get(words[s]) || 0)) shared--;
      }
      return starts;
    }

    // Las palabras que tienen en común (contando repetidas): el LCS no pasa de ahí.
    function sharedWords(quote, words) {
      const left = new Map();
      for (const w of quote) left.set(w, (left.get(w) || 0) + 1);
      let shared = 0;
      for (const w of words) {
        const k = left.get(w);
        if (k) { left.set(w, k - 1); shared++; }
      }
      return shared;
    }

    // El parecido del bloque entero (0 si ni con todas las palabras en común llegaría al mínimo).
    function wholeScore(quote, words) {
      const n = quote.length, m = words.length;
      if (!m || 2 * sharedWords(quote, words) / (n + m) < MIN_LIKENESS) return 0;
      return 2 * lcsFrom(quote, words, 0, m)[m] / (n + m);
    }

    // Dentro de un bloque: el bloque entero y, si la cita es más corta, cada tramo seguido de entre 0,7 y 1,3 veces su
    // largo. Devuelve { score, from, to } (índices de palabras) o null si ninguno puede llegar al mínimo.
    function bestSpan(quote, words) {
      const n = quote.length, m = words.length;
      if (!m) return null;
      const spans = n < m && n <= MAX_SPAN_WORDS;
      const minLen = spans ? Math.ceil(0.7 * n) : m;
      const maxLen = spans ? Math.ceil(1.3 * n) : m;
      // El LCS no pasa de las palabras en común: si ni así llega al mínimo, el bloque no se compara.
      if (2 * sharedWords(quote, words) / (n + minLen) < MIN_LIKENESS) return null;
      const whole = lcsFrom(quote, words, 0, m);
      let best = { score: 2 * whole[m] / (n + m), from: 0, to: m - 1 };
      if (!spans) return best;
      const inQuote = new Set(quote);
      for (let s = 0; s + minLen <= m; s++) {
        // Un tramo que empieza con una palabra que no está en la cita nunca gana al que empieza en la siguiente.
        if (!inQuote.has(words[s]) && s + minLen < m) continue;
        const lcs = lcsFrom(quote, words, s, maxLen);
        for (let len = minLen; len < lcs.length; len++) {
          const score = 2 * lcs[len] / (n + len);
          if (score > best.score) best = { score, from: s, to: s + len - 1 };
        }
      }
      return best;
    }

    // El tramo que gana suele dejar afuera las palabras cambiadas de sus bordes (sin ellas se parece más). Para que se
    // vea qué cambió, los bordes de la cita se ubican a través de las coincidencias: antes de la primera, tantas
    // palabras como le quedan a la cita antes de la suya, y lo mismo después de la última. Sin salir de [lo, hi].
    function project(quote, words, from, to, lo, hi) {
      const n = quote.length, len = to - from + 1;
      const rest = Array.from({ length: n + 1 }, () => new Int32Array(len + 1)); // rest[i][j] = LCS(quote[i..], tramo[j..])
      for (let i = n - 1; i >= 0; i--) {
        for (let j = len - 1; j >= 0; j--) {
          rest[i][j] = quote[i] === words[from + j] ? rest[i + 1][j + 1] + 1 : Math.max(rest[i + 1][j], rest[i][j + 1]);
        }
      }
      let first = null, last = null;
      for (let i = 0, j = 0; i < n && j < len;) {
        if (quote[i] === words[from + j]) {
          first = first || [i, j];
          last = [i, j];
          i++;
          j++;
        } else if (rest[i + 1][j] >= rest[i][j + 1]) {
          i++;
        } else {
          j++;
        }
      }
      if (!first) return [from, to];
      return [Math.max(lo, from + first[1] - first[0]), Math.min(hi, from + last[1] + n - 1 - last[0])];
    }

    // Una sola pasada da el LCS de la cita contra cada tramo que empieza en "start": lcs[len], con len hasta maxLen.
    function lcsFrom(quote, words, start, maxLen) {
      const n = quote.length;
      const count = Math.min(maxLen, words.length - start);
      const lcs = new Int32Array(count + 1);
      let previous = new Int32Array(n + 1), row = new Int32Array(n + 1);
      for (let j = 1; j <= count; j++) {
        const w = words[start + j - 1];
        for (let i = 1; i <= n; i++) {
          row[i] = quote[i - 1] === w ? previous[i - 1] + 1 : Math.max(previous[i], row[i - 1]);
        }
        lcs[j] = row[n];
        [previous, row] = [row, previous];
      }
      return lcs;
    }

    function anchorAll(report) {
      for (const n of list) {
        const where = locate(n);
        n.range = where ? where.range : null;
        n.seg = where ? where.seg : null;
        n.endSeg = where ? where.endSeg : null;
        n.shownLine = where ? where.line : n.line;
        n.current = where && where.current ? where.current : null;
      }
      if (report && list.length) {
        post({
          type: 'notesAnchored',
          results: list.map((n) => ({ id: n.id, found: !!n.range, startSeg: n.seg, endSeg: n.endSeg, current: n.current })),
        });
      }
    }

    // Lo que manda la app al abrir el documento o tras crear, borrar o restaurar; editId abre esa tarjeta para escribir
    // y flashId la destella sin mover el foco (una marca para revisar).
    function set(items, show, editId, flashId) {
      // Una marca para revisar (flashId sin editId) no cierra el apunte que se está escribiendo: sigue abierto, con lo
      // escrito, el cursor y el foco. Si no, y se estaba escribiendo otro apunte (y se creó uno nuevo), lo escrito se
      // guarda antes de redibujar.
      const editor = editingId ? editorFor(editingId) : null;
      const keep = !editId && flashId && editingId && items.some((n) => n.id === editingId) ? editingId : null;
      const kept = keep && editor
        ? {
          value: editor.value, start: editor.selectionStart, end: editor.selectionEnd,
          focused: document.activeElement === editor, floating: !!(popover && popover.getAttribute('data-for') === keep),
        }
        : null;
      const draft = editor && !keep && editingId !== editId ? { id: editingId, text: editor.value.trim() } : null;
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
      editingId = editId || keep || null;
      reportEditing();
      newId = editId || (keep ? newId : null); // si el que sigue abierto era nuevo, vacío todavía se descarta
      pinnedId = editId || (byId(pinnedId) ? pinnedId : null);
      render();
      if (editId) {
        activate(editId);
        focusEditor(editId);
        return;
      }
      if (kept) restoreEditor(keep, kept);
      if (flashId && visible) flash(flashId);
    }

    // El editor que siguió abierto al redibujar: lo escrito, el cursor y, si lo tenía, el foco (sin mover la vista).
    function restoreEditor(id, kept) {
      if (kept.floating && compact) openPopover(id);
      const box = editorFor(id);
      if (!box) return;
      box.value = kept.value;
      box.setSelectionRange(kept.start, kept.end);
      if (kept.focused) box.focus({ preventScroll: true });
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
        '<div class="notes-head"><div class="notes-title-row"><strong class="notes-title"></strong>' +
        '<button type="button" class="note-icon" data-act="previous" title="Apunte anterior (Alt+↑)" aria-label="Apunte anterior">&#xE70E;</button>' +
        '<button type="button" class="note-icon" data-act="next" title="Apunte siguiente (Alt+↓)" aria-label="Apunte siguiente">&#xE70D;</button></div>' +
        '<button type="button" data-act="export" title="Copia los apuntes pendientes, con sus referencias, para pegarlos en el chat de una IA">Copiar para la IA</button>' +
        '<button type="button" data-act="clear">Borrar resueltos</button>' +
        '<div class="notes-orphans" hidden><span></span> · <button type="button" data-act="resolveOrphans">Marcar resueltos</button></div></div>' +
        '<div class="notes-cards"></div>';
      markers = make('div', 'lector-note-markers lector-note-ui');
      dots = make('div', 'note-marker-list');
      markers.appendChild(dots);
      const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      svg.setAttribute('class', 'lector-note-link');
      svg.setAttribute('aria-hidden', 'true');
      link = document.createElementNS('http://www.w3.org/2000/svg', 'path');
      svg.appendChild(link);
      document.body.append(svg, aside, markers);
      for (const host of [aside, markers]) {
        host.addEventListener('click', onClick);
        // Elegir el tipo mientras se escribe no le quita el foco al cuadro de la nota.
        host.addEventListener('mousedown', (e) => {
          if (e.target instanceof Element && e.target.closest('[data-act="kind"]')) e.preventDefault();
        });
        host.addEventListener('change', onChange);
        host.addEventListener('keydown', onKey);
        host.addEventListener('focusout', onFocusOut);
        host.addEventListener('mouseover', (e) => activate(cardId(e.target) || pinnedId));
        host.addEventListener('mouseleave', () => activate(pinnedId));
      }
      // Entre dos tarjetas (el espacio que las separa) se queda desplegada la última: así no tiemblan.
      aside.addEventListener('mouseover', (e) => {
        const id = cardId(e.target);
        if (id) hover(id);
        else if (e.target instanceof Element && e.target.closest('.notes-head')) hover(null);
      });
      aside.addEventListener('mouseleave', () => hover(null));
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
        : '<div class="note-text" data-act="edit" title="Clic para editar">' + (n.text ? esc(n.text) : '<span class="note-empty">Por revisar · sin nota</span>') + '</div>';
      // Si la cita se retocó, la original tachada y lo que dice ahora (compacta, solo una línea de lo actual).
      const quote = n.range && n.current
        ? '<div class="note-changed-label">La cita cambió</div>' +
          '<blockquote class="note-quote note-quote-old" title="Cita original">' + esc(n.quote) + '</blockquote>' +
          '<blockquote class="note-quote note-quote-new" title="Ahora dice">' + esc(n.current) + '</blockquote>'
        : '<blockquote class="note-quote">' + esc(n.quote) + '</blockquote>';
      return '<article class="note-card kind-' + kind.toLowerCase() + (n.resolved ? ' resolved' : '') + (n.range ? (n.current ? ' changed' : '') : ' orphan') +
        (activeId === n.id ? ' active' : '') + (pinnedId === n.id || editingId === n.id ? ' pinned' : '') +
        (hoverId === n.id ? ' hovered' : '') + '" data-id="' + esc(n.id) + '">' +
        '<div class="note-top"><div class="note-kinds" role="radiogroup" aria-label="Tipo de apunte">' + kinds + '</div>' +
        '<span class="note-where" title="' + (n.range ? 'Línea en el Markdown' : 'La cita ya no está en el documento') + '">' + where + '</span></div>' +
        quote + body +
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
      const orphans = list.filter((n) => !n.resolved && !n.range).length;
      const orphanRow = aside.querySelector('.notes-orphans');
      orphanRow.hidden = orphans === 0;
      orphanRow.firstChild.textContent = orphans === 1 ? '1 apunte ya no encuentra su cita' : orphans + ' apuntes ya no encuentran su cita';
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
      drawLink();
    }

    // La tarjeta elegida queda justo en su cita y sigue marcada aunque el mouse se vaya.
    function pin(id) {
      pinnedId = id;
      for (const card of aside.querySelectorAll('.note-card')) {
        const cardFor = card.getAttribute('data-id');
        card.classList.toggle('pinned', cardFor === id || cardFor === editingId);
      }
      activate(id);
      layout();
    }

    function flash(id) {
      const card = aside.querySelector('.note-card[data-id="' + CSS.escape(id) + '"]');
      if (!card) return;
      card.classList.remove('flash');
      void card.offsetWidth;
      card.classList.add('flash');
    }

    // Alt+↓ / Alt+↑ o las flechas de la columna: el apunte siguiente o anterior en el texto; del último vuelve al
    // primero. Sin uno elegido, se parte de lo que se ve.
    function go(step) {
      const placed = list.filter((n) => n.range).sort((a, b) => position(a) - position(b));
      if (!visible || !placed.length) return;
      let index = placed.findIndex((n) => n.id === pinnedId);
      if (index >= 0) {
        index = (index + step + placed.length) % placed.length;
      } else {
        const top = window.scrollY;
        index = step > 0 ? placed.findIndex((n) => position(n) >= top) : placed.findLastIndex((n) => position(n) < top);
        if (index < 0) index = step > 0 ? 0 : placed.length - 1;
      }
      const n = placed[index];
      pauseFollow(); // si se está leyendo, que la lectura no se lleve la vista enseguida
      pin(n.id);
      window.scrollTo({ top: Math.max(0, position(n) - window.innerHeight * 0.3), behavior: 'smooth' });
      if (compact) openPopover(n.id);
      else flash(n.id);
    }

    function queueLayout() {
      if (queued) return;
      queued = true;
      requestAnimationFrame(() => {
        queued = false;
        layout();
      });
    }

    // Cada tarjeta a la altura de su cita. Si se pisan, se corren hacia abajo, salvo la elegida: esa queda justo en
    // su cita y las de arriba se corren hacia arriba (como en Google Docs). Sin ancho, marcas en el margen.
    function layout() {
      if (!aside || aside.hidden) {
        drawLink();
        return;
      }
      const doc = document.querySelector('main.doc');
      if (!doc) return;
      compact = window.innerWidth < MIN_DOC + COLUMN + 64;
      document.body.classList.toggle('notes-compact', compact);
      const docRect = doc.getBoundingClientRect();
      const height = document.documentElement.scrollHeight;
      if (compact) {
        layoutMarkers(docRect);
        drawLink();
        return;
      }
      dots.innerHTML = '';
      closePopover();
      aside.style.left = (docRect.right + window.scrollX - 24) + 'px';
      aside.style.height = height + 'px';
      const gap = 10;
      const hovered = hoverId ? aside.querySelector('.note-card.hovered') : null;
      if (hovered) hovered.classList.remove('hovered'); // se mide achicada; desplegada la acomoda expandHovered()
      const items = Array.from(aside.querySelectorAll('.note-card'), (card) => {
        const n = byId(card.getAttribute('data-id'));
        return { card, id: n && n.id, wanted: n && n.range ? position(n) : null, height: card.offsetHeight, top: 0 };
      });
      const pinned = items.findIndex((it) => it.id === pinnedId && it.wanted !== null);
      if (pinned >= 0) {
        items[pinned].top = items[pinned].wanted;
        for (let i = pinned - 1; i >= 0; i--) {
          const limit = items[i + 1].top - gap - items[i].height;
          items[i].top = items[i].wanted === null ? limit : Math.min(items[i].wanted, limit);
        }
      }
      // Ninguna encima del encabezado de la columna ni de la anterior (eso puede bajar también a la elegida).
      let bottom = aside.querySelector('.notes-head').offsetHeight + 8;
      baseTops.clear();
      items.forEach((it, i) => {
        it.top = Math.max(i <= pinned ? it.top : it.wanted ?? bottom, bottom);
        baseTops.set(it.id, it.top);
        it.card.style.top = it.top + 'px';
        bottom = it.top + it.height + gap;
      });
      aside.style.height = Math.max(height, bottom) + 'px';
      if (hovered) {
        hovered.classList.add('hovered');
        expandHovered();
      }
      drawLink();
    }

    function hover(id) {
      if (!aside || id === hoverId) return;
      hoverId = id;
      for (const card of aside.querySelectorAll('.note-card')) card.classList.toggle('hovered', card.getAttribute('data-id') === id);
      if (compact || aside.hidden) return;
      expandHovered();
      drawLink();
    }

    // La del mouse se despliega donde está (no se mueve bajo el puntero) y corre hacia abajo a las que pisa; las demás
    // vuelven a donde las dejó layout().
    function expandHovered() {
      let bottom = -Infinity;
      let below = false;
      for (const card of aside.querySelectorAll('.note-card')) {
        const id = card.getAttribute('data-id');
        const base = baseTops.has(id) ? baseTops.get(id) : parseFloat(card.style.top);
        let top = base;
        if (id === hoverId) {
          top = parseFloat(card.style.top);
          below = true;
        } else if (below) {
          top = Math.max(base, bottom);
        }
        card.style.top = top + 'px';
        bottom = top + card.offsetHeight + 10;
      }
      aside.style.height = Math.max(parseFloat(aside.style.height) || 0, bottom) + 'px';
    }

    // Una línea punteada de la cita a su tarjeta, para la marcada (la del mouse o la elegida).
    function drawLink() {
      if (!link) return;
      const n = activeId && visible && !compact ? byId(activeId) : null;
      const card = n && n.range ? aside.querySelector('.note-card[data-id="' + CSS.escape(n.id) + '"]') : null;
      const line = card ? Array.from(n.range.getClientRects()).find((r) => r.width > 0) : null;
      if (!line) {
        link.removeAttribute('d');
        return;
      }
      const x = line.right + window.scrollX + 4;
      const y = line.top + window.scrollY + line.height / 2;
      const left = parseFloat(aside.style.left);
      const top = parseFloat(card.style.top) + 16;
      link.setAttribute('d', 'M' + x + ' ' + y + 'H' + Math.max(x, left - 16) + 'L' + left + ' ' + top);
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
      reportEditing();
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
      reportEditing();
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
      reportEditing();
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
      if (editingId === id) {
        editingId = null;
        reportEditing();
      }
      if (pinnedId === id) pinnedId = null;
      if (hoverId === id) hoverId = null;
      post({ type: 'noteDelete', id, silent: !!silent });
      if (popover && popover.getAttribute('data-for') === id) closePopover();
      render();
      if (!silent) showUndo('Apunte borrado', () => post({ type: 'noteRestore', id }));
    }

    // «Marcar resueltos» de la cabecera: los pendientes que ya no encuentran su cita, de una vez y con aviso para deshacer.
    function resolveOrphans() {
      const ids = list.filter((n) => !n.resolved && !n.range).map((n) => n.id);
      if (!ids.length) return;
      setResolved(ids, true);
      showUndo(ids.length === 1 ? '1 apunte marcado como resuelto' : ids.length + ' apuntes marcados como resueltos', () => setResolved(ids, false));
    }

    function setResolved(ids, resolved) {
      for (const id of ids) {
        const n = byId(id);
        if (!n || n.resolved === resolved) continue;
        n.resolved = resolved;
        post({ type: 'noteUpdate', id, resolved });
      }
      render();
    }

    function showUndo(text, undo) {
      if (!undoBar) {
        undoBar = make('div', 'lector-undo lector-note-ui');
        undoBar.setAttribute('role', 'status');
        document.body.appendChild(undoBar);
        undoBar.addEventListener('click', (e) => {
          if (!(e.target instanceof Element && e.target.closest('[data-undo]'))) return;
          undoBar.hidden = true;
          const action = undoAction;
          undoAction = null;
          if (action) action();
        });
      }
      undoAction = undo;
      undoBar.innerHTML = esc(text) + ' <button type="button" data-undo>Deshacer</button>';
      undoBar.hidden = false;
      clearTimeout(undoTimer);
      undoTimer = setTimeout(() => { undoBar.hidden = true; }, 6000);
    }

    function onClick(e) {
      const target = e.target instanceof Element ? e.target.closest('[data-act]') : null;
      const act = target ? target.getAttribute('data-act') : null;
      const id = cardId(e.target);
      // Tocar una tarjeta de la columna la lleva a la altura de su cita.
      if (id && act !== 'delete' && !compact && byId(id)) pin(id);
      if (!act) return;
      if (act === 'export') post({ type: 'notesExport' });
      else if (act === 'clear') post({ type: 'notesClearResolved' });
      else if (act === 'resolveOrphans') resolveOrphans();
      else if (act === 'previous' || act === 'next') go(act === 'next' ? 1 : -1);
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
        if (e.key === 'Escape' && popover) {
          e.preventDefault(); // cerrar la tarjeta flotante no detiene la lectura
          closePopover();
        }
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

    // Tecla M: marca el bloque entero para revisar, sin texto y sin abrir el editor. Si ya hay un apunte pendiente con
    // ese mismo texto, solo avisa a la app.
    function mark(anchor) {
      const el = elementsFor(anchor)[0];
      const text = el ? el.textContent.replace(/\s+/g, ' ').trim() : '';
      if (!text) return;
      const folded = textIndex.fold(text).replace(/\s+/g, ' ').trim();
      if (list.some((n) => !n.resolved && textIndex.fold(n.quote).replace(/\s+/g, ' ').trim() === folded)) {
        post({ type: 'noteMarkDuplicate' });
        return;
      }
      post({ type: 'noteCreate', quote: text, startSeg: anchor, endSeg: anchor, quiet: true });
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
      pin(hit.id);
      if (compact) openPopover(hit.id);
      else flash(hit.id);
    });

    return { set, setVisible, refresh, rememberSelection, createFromSelection, createForSegment, createHere, mark, closeMenu, go };
  })();

  // Aviso de la app con botones al pie de la página («El archivo cambió en el disco»), con el estilo del de «Deshacer»
  // de los apuntes. Se queda hasta que se elige algo: cada botón le manda su comando a la app y cierra el aviso.
  const notice = (() => {
    let bar = null;

    // actions: [{ id, label }]; id es el comando que recibe la app. Sin botones es solo una indicación («quiet»):
    // queda por debajo de los apuntes y no recibe el mouse, así no tapa ni le quita el foco al que se escribe.
    function show(text, actions) {
      if (!bar) {
        bar = Object.assign(document.createElement('div'), { className: 'lector-notice lector-note-ui' });
        bar.setAttribute('role', 'status');
        document.body.appendChild(bar);
        bar.addEventListener('click', (e) => {
          const button = e.target instanceof Element ? e.target.closest('[data-notice]') : null;
          if (!button) return;
          hide();
          post({ type: 'command', command: button.getAttribute('data-notice') });
        });
      }
      bar.replaceChildren(Object.assign(document.createElement('span'), { textContent: text }));
      for (const action of actions || []) {
        const button = Object.assign(document.createElement('button'), { type: 'button', textContent: action.label });
        button.setAttribute('data-notice', action.id);
        bar.appendChild(button);
      }
      bar.classList.toggle('quiet', !(actions && actions.length));
      bar.hidden = false;
      document.body.classList.add('has-notice');
    }

    function hide() {
      if (bar) bar.hidden = true;
      document.body.classList.remove('has-notice');
    }

    return { show, hide };
  })();

  // Lo ya escuchado (lo decide la app): una línea tenue en el margen izquierdo de cada bloque. Todas van en una misma
  // columna, 24 px a la izquierda del texto, aunque el bloque esté en una lista, una cita o una tabla: aquí se calcula
  // dónde cae esa columna respecto del borde de cada bloque (--heard-left) y lector.css dibuja la línea.
  const coverage = (() => {
    const GUTTER = 24;

    function set(anchors) {
      const wanted = new Set(anchors || []);
      const heard = new Set();
      for (const el of document.querySelectorAll('[data-seg]')) {
        if (wanted.has(el.getAttribute('data-seg'))) heard.add(visualTarget(el));
      }

      // Primero se mide todo y después se escribe, para que la página no rehaga su diseño por cada bloque.
      const main = document.querySelector('main.doc');
      const column = main ? main.getBoundingClientRect().left + parseFloat(getComputedStyle(main).paddingLeft) - GUTTER : 0;
      const lefts = new Map();
      for (const el of heard) {
        // Una fila se corre con la tabla cuando esta se desplaza a lo ancho: se mide como si no lo estuviera.
        const table = el.tagName === 'TR' ? el.closest('table') : null;
        lefts.set(el, column - el.getBoundingClientRect().left - (table ? table.scrollLeft : 0));
      }

      for (const el of document.querySelectorAll('.lector-heard')) {
        if (lefts.has(el)) continue;
        el.classList.remove('lector-heard');
        el.style.removeProperty('--heard-left');
      }
      for (const [el, left] of lefts) {
        el.style.setProperty('--heard-left', left + 'px');
        el.classList.add('lector-heard');
      }
    }

    return { set };
  })();

  // Archivos soltados sobre la página: no se navega a ellos (la página no sabe su ruta); se le pasan a la app, que los
  // abre en pestañas. Arrastrar texto seleccionado sigue igual. Al entrar, la app tapa la ventana con su capa de
  // "Suelta para abrir", que es la que suele recibirlos; lo que se arrastra desde la propia página (una imagen) no la abre.
  const draggingFiles = (e) => e.dataTransfer && Array.from(e.dataTransfer.types).includes('Files');
  let draggingFromPage = false;
  document.addEventListener('dragstart', () => { draggingFromPage = true; });
  document.addEventListener('dragend', () => { draggingFromPage = false; });
  for (const type of ['dragenter', 'dragover']) {
    document.addEventListener(type, (e) => {
      if (!draggingFiles(e)) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'copy';
      if (type === 'dragenter' && !draggingFromPage) post({ type: 'dragFiles' });
    });
  }
  document.addEventListener('drop', (e) => {
    if (!draggingFiles(e)) return;
    e.preventDefault();
    if (window.chrome && window.chrome.webview && e.dataTransfer.files.length) {
      window.chrome.webview.postMessageWithAdditionalObjects({ type: 'drop' }, e.dataTransfer.files);
    }
  });

  // La palabra que suena, dentro del bloque resaltado. La app manda las palabras dichas de cada segmento del bloque
  // (un bloque largo se parte en varios) y luego solo cuál suena. Lo dicho no siempre está escrito («Código en C#:», el
  // encabezado antes de cada celda, el diccionario): se empareja en orden con las palabras que se ven (LCS sobre las
  // palabras plegadas) y, si la que suena no tiene pareja, queda marcada la última que sí la tuvo.
  const word = (() => {
    const MAX_CELLS = 2000000; // por encima, emparejamiento simple hacia adelante
    let anchor = null;
    let lastParts = null;
    let ranges = null; // ranges[parte][palabra] = Range o null

    function clear() {
      if (window.CSS && CSS.highlights) CSS.highlights.delete('lector-word');
    }

    const subwords = (s) => textIndex.fold(s).split(/[^\p{L}\p{N}]+/u).filter(Boolean);

    function set(newAnchor, parts) {
      anchor = newAnchor;
      lastParts = parts;
      ranges = null;
      clear();
      if (!anchor || !parts) return;
      const shown = [];
      for (const block of textIndex.blocks().filter((b) => b.seg === anchor)) {
        const text = textIndex.slice(block.start, block.end);
        for (const m of text.matchAll(/[\p{L}\p{N}]+/gu)) {
          shown.push({ word: m[0], start: block.start + m.index, end: block.start + m.index + m[0].length });
        }
      }
      const spoken = []; // { word, part, index }: las palabras dichas partidas como en la página («appsettings.json»)
      parts.forEach((tokens, part) => tokens.forEach((token, index) => {
        for (const w of subwords(token)) spoken.push({ word: w, part, index });
      }));
      const pairs = align(spoken.map((s) => s.word), shown.map((s) => s.word));
      // Cada palabra dicha abarca desde la primera hasta la última de sus partes que tuvo pareja.
      const spans = parts.map((tokens) => tokens.map(() => null));
      pairs.forEach((j, i) => {
        if (j < 0) return;
        const { part, index } = spoken[i];
        const span = spans[part][index];
        if (span) span.end = shown[j].end;
        else spans[part][index] = { start: shown[j].start, end: shown[j].end };
      });
      ranges = spans.map((list) => list.map((s) => (s ? textIndex.rangeOf(s.start, s.end) : null)));
    }

    // Para cada palabra de a, la de b con la que se empareja (o -1), en orden.
    function align(a, b) {
      const n = a.length, m = b.length;
      const pairs = new Array(n).fill(-1);
      if (!n || !m) return pairs;
      if ((n + 1) * (m + 1) > MAX_CELLS) {
        for (let i = 0, j = 0; i < n; i++) {
          const k = b.indexOf(a[i], j);
          if (k >= 0 && k - j < 8) { pairs[i] = k; j = k + 1; }
        }
        return pairs;
      }
      const dp = new Int32Array((n + 1) * (m + 1));
      const at = (i, j) => i * (m + 1) + j;
      for (let i = n - 1; i >= 0; i--) {
        for (let j = m - 1; j >= 0; j--) {
          dp[at(i, j)] = a[i] === b[j] ? dp[at(i + 1, j + 1)] + 1 : Math.max(dp[at(i + 1, j)], dp[at(i, j + 1)]);
        }
      }
      for (let i = 0, j = 0; i < n && j < m;) {
        if (a[i] === b[j]) { pairs[i] = j; i++; j++; }
        else if (dp[at(i + 1, j)] >= dp[at(i, j + 1)]) i++;
        else j++;
      }
      return pairs;
    }

    // parts solo llega al cambiar de segmento; index -1: todavía no suena ninguna palabra del segmento.
    function show(newAnchor, part, index, parts) {
      if (parts !== undefined || newAnchor !== anchor) set(newAnchor, parts || null);
      const range = ranges && ranges[part] && index >= 0 ? ranges[part][index] : null;
      if (!range || !(window.CSS && CSS.highlights)) return;
      const marked = new Highlight(range);
      marked.priority = -1; // debajo de la búsqueda
      CSS.highlights.set('lector-word', marked);
      // Un bloque más alto que la pantalla (código, tabla, párrafo largo): si la palabra sale de la vista, se la sigue.
      if (follow) {
        const rect = range.getBoundingClientRect();
        const height = window.innerHeight || document.documentElement.clientHeight;
        if (rect.height && (rect.top < 0 || rect.bottom > height * 0.9)) {
          window.scrollBy({ top: rect.top - height * 0.4, behavior: 'smooth' });
        }
      }
    }

    return {
      show,
      clear: () => { anchor = null; lastParts = null; ranges = null; clear(); },
      anchor: () => anchor,
      // El texto cambió (diagramas): los rangos viejos ya no sirven; la próxima palabra los vuelve a armar.
      refresh: () => { if (anchor) set(anchor, lastParts); },
    };
  })();

  // Cambió el texto de la página (se dibujaron los diagramas): índice, búsqueda y apuntes al día.
  function textChanged() {
    textIndex.invalidate();
    find.refresh();
    notes.refresh();
    word.refresh();
  }

  window.lector = { highlight, scrollToAnchor, scrollToHeading, scrollToY, visibleBlock, scrollToBlock, find, diagrams, setColors, notes, notice, coverage, word: word.show };
})();
