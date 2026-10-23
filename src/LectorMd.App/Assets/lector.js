// Puente entre el documento renderizado y la app: resaltado, doble clic, enlaces y atajos.
(() => {
  'use strict';

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

  function highlight(anchor) {
    for (const el of highlighted) {
      el.classList.remove('lector-reading');
    }
    highlighted = anchor ? elementsFor(anchor) : [];
    for (const el of highlighted) {
      el.classList.add('lector-reading');
    }

    const first = highlighted[0];
    if (first && follow) {
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

  document.addEventListener('keydown', (e) => {
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

    if (command) {
      e.preventDefault();
      post({ type: 'command', command });
    }
  });

  window.lector = { highlight, scrollToAnchor };
})();
