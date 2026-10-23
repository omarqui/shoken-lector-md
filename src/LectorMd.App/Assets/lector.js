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

  // Búsqueda (Ctrl+F): sin distinguir acentos ni mayúsculas. Se compara una copia "plegada" del texto que recuerda
  // de qué nodo y posición salió cada carácter, y los resultados se pintan con la API de resaltado de CSS, sin tocar
  // el DOM. La app recibe cuántos hay en cada bloque para mostrarlos en el índice.
  const find = (() => {
    const MAX_MATCHES = 5000;
    const fold = (s) => s.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();
    let bar = null;
    let input = null;
    let count = null;
    let playButton = null;
    let index = null;
    let matches = []; // { range, seg }
    let current = -1;
    let timer = 0;

    // El texto no cambia mientras la página está abierta: se indexa una sola vez.
    function buildIndex() {
      const root = document.querySelector('main.doc') || document.body;
      const nodes = [], segs = [], nodeOf = [], offsetOf = [];
      let text = '';
      let space = true;
      let lastSeg = null;
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
        acceptNode: (n) => (n.parentElement && n.parentElement.closest('script, style')
          ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT),
      });
      for (let n = walker.nextNode(); n; n = walker.nextNode()) {
        const block = n.parentElement.closest('[data-seg]');
        lastSeg = block ? block.getAttribute('data-seg') : lastSeg; // fuera de un bloque legible: el anterior
        const k = nodes.push(n) - 1;
        segs.push(lastSeg);
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
      return { text, nodes, segs, nodeOf, offsetOf };
    }

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
        index = index || buildIndex();
        const { text, nodes, segs, nodeOf, offsetOf } = index;
        for (let at = text.indexOf(query); at >= 0 && matches.length < MAX_MATCHES; at = text.indexOf(query, at + query.length)) {
          const end = at + query.length - 1;
          const range = document.createRange();
          range.setStart(nodes[nodeOf[at]], offsetOf[at]);
          range.setEnd(nodes[nodeOf[end]], offsetOf[end] + 1);
          matches.push({ range, seg: segs[nodeOf[at]] });
        }
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

    // El texto de la página cambió (se dibujaron los diagramas): se vuelve a indexar y a buscar.
    function refresh() {
      index = null;
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

  // Selección de 1 a 3 palabras: la app muestra un botón para editar su pronunciación. Se espera un instante
  // porque el doble clic (leer desde aquí) también selecciona una palabra y luego la quita.
  let selectionTimer = 0;
  let selectionShown = false;
  document.addEventListener('mouseup', () => {
    clearTimeout(selectionTimer);
    selectionTimer = setTimeout(() => {
      const selection = window.getSelection();
      const text = selection ? selection.toString().trim() : '';
      const words = text.split(/\s+/).filter(Boolean);
      if (!selection || selection.rangeCount === 0 || text.length === 0 || text.length > 40 || words.length > 3 || /\n/.test(text)) {
        if (selectionShown) {
          selectionShown = false;
          post({ type: 'selectionCleared' });
        }
        return;
      }
      const rect = selection.getRangeAt(0).getBoundingClientRect();
      const node = selection.anchorNode && selection.anchorNode.nodeType === 1 ? selection.anchorNode : selection.anchorNode && selection.anchorNode.parentElement;
      const block = node ? node.closest('[data-seg]') : null;
      selectionShown = true;
      post({
        type: 'selection',
        text: text.replace(/^[^\p{L}\p{N}#.]+|[^\p{L}\p{N}#+]+$/gu, ''),
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
      find.refresh(); // el texto de los diagramas cambió: los resultados de la búsqueda también
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

    return { count: blocks.length, rememberScroll };
  })();

  window.lector = { highlight, scrollToAnchor, scrollToHeading, scrollToY, find, diagrams };
})();
