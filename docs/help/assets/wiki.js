/* 3commerce wiki — interaction layer. Vanilla, no deps, progressive-enhancement. */
(function () {
  'use strict';

  /* ---- theme (persisted, respects system on first visit) --------------- */
  var root = document.documentElement;
  var saved = localStorage.getItem('3c-theme');
  if (saved) {
    root.setAttribute('data-theme', saved);
  } else if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
    root.setAttribute('data-theme', 'dark');
  }
  function toggleTheme() {
    var next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    localStorage.setItem('3c-theme', next);
    paintThemeBtn();
  }
  function paintThemeBtn() {
    var b = document.querySelector('[data-theme-toggle]');
    if (!b) return;
    var dark = root.getAttribute('data-theme') === 'dark';
    b.textContent = dark ? '☀' : '☾';
    b.setAttribute('aria-pressed', dark ? 'true' : 'false');
    b.setAttribute('aria-label', dark ? 'Switch to light theme' : 'Switch to dark theme');
  }

  document.addEventListener('DOMContentLoaded', function () {
    paintThemeBtn();
    var tb = document.querySelector('[data-theme-toggle]');
    if (tb) tb.addEventListener('click', toggleTheme);

    /* ---- mobile nav drawer -------------------------------------------- */
    var layout = document.querySelector('.layout');
    var menu = document.querySelector('.menu-btn');
    if (menu && layout) {
      menu.setAttribute('aria-expanded', 'false');
      menu.addEventListener('click', function () {
        var open = layout.classList.toggle('nav-open');
        menu.setAttribute('aria-expanded', open ? 'true' : 'false');
      });
      var scrim = document.querySelector('.scrim');
      if (scrim) scrim.addEventListener('click', function () {
        layout.classList.remove('nav-open');
        menu.setAttribute('aria-expanded', 'false');
      });
    }

    /* ---- reading progress --------------------------------------------- */
    var bar = document.getElementById('progress');
    if (bar) {
      var onScroll = function () {
        var h = document.documentElement;
        var max = h.scrollHeight - h.clientHeight;
        bar.style.width = (max > 0 ? (h.scrollTop / max) * 100 : 0) + '%';
      };
      window.addEventListener('scroll', onScroll, { passive: true });
      onScroll();
    }

    /* ---- heading anchors + auto TOC ----------------------------------- */
    var main = document.querySelector('.content-inner');
    var tocNav = document.querySelector('.toc nav');
    var headings = main ? main.querySelectorAll('h2[id], h3[id]') : [];
    if (tocNav && headings.length) {
      headings.forEach(function (h) {
        var a = document.createElement('a');
        a.href = '#' + h.id;
        a.textContent = h.textContent.replace('¶', '').trim();
        if (h.tagName === 'H3') a.className = 'sub';
        tocNav.appendChild(a);
        // hover anchor link
        var anc = document.createElement('a');
        anc.href = '#' + h.id; anc.className = 'anchor'; anc.textContent = '¶';
        anc.setAttribute('aria-label', 'Link to this section');
        h.appendChild(anc);
      });

      var links = tocNav.querySelectorAll('a');
      var byId = {};
      links.forEach(function (l) { byId[l.getAttribute('href').slice(1)] = l; });
      // Track which headings are above the fold; the lowest one that's passed the top
      // line is "current". This stays correct on short pages and at the very bottom.
      var seen = {};
      var setActive = function () {
        var current = null;
        headings.forEach(function (h) { if (seen[h.id]) current = h.id; });
        // At the page bottom, force-activate the final heading.
        var atBottom = window.innerHeight + window.scrollY >= document.body.scrollHeight - 4;
        if (atBottom && headings.length) current = headings[headings.length - 1].id;
        links.forEach(function (l) { l.classList.remove('active'); });
        if (current && byId[current]) byId[current].classList.add('active');
      };
      var spy = new IntersectionObserver(function (entries) {
        entries.forEach(function (e) { seen[e.target.id] = e.boundingClientRect.top < window.innerHeight * 0.25; });
        setActive();
      }, { rootMargin: '0px 0px -75% 0px', threshold: [0, 1] });
      headings.forEach(function (h) { spy.observe(h); });
      window.addEventListener('scroll', setActive, { passive: true });
    }

    /* ---- copy buttons on code blocks ---------------------------------- */
    document.querySelectorAll('pre').forEach(function (pre) {
      if (!pre.querySelector('code')) return;
      var btn = document.createElement('button');
      btn.className = 'copy-btn'; btn.type = 'button'; btn.textContent = 'Copy';
      btn.addEventListener('click', function () {
        navigator.clipboard.writeText(pre.querySelector('code').innerText).then(function () {
          btn.textContent = 'Copied'; setTimeout(function () { btn.textContent = 'Copy'; }, 1400);
        });
      });
      pre.appendChild(btn);
    });

    buildPalette();
  });

  /* ---- command palette (press / or Cmd/Ctrl-K) ----------------------- */
  function buildPalette() {
    // Page index — kept in sync with the nav.
    var pages = [
      { t: 'Overview', p: 'index.html', k: 'I' },
      { t: 'Getting started', p: 'getting-started.html', k: 'G' },
      { t: 'Storefront operations', p: 'storefront-operations.html', k: 'S' },
      { t: 'Admin operations', p: 'admin-operations.html', k: 'A' },
      { t: 'Testing', p: 'testing.html', k: 'T' },
      { t: 'Deployment', p: 'deployment.html', k: 'D' },
      { t: 'Project analysis', p: 'project-analysis.html', k: 'P' }
    ];
    var here = location.pathname.split('/').pop() || 'index.html';
    // Add this page's headings as jump targets.
    var entries = pages.map(function (x) { return { type: 'page', t: x.t, p: x.p, k: x.k }; });
    document.querySelectorAll('.content-inner h2[id], .content-inner h3[id]').forEach(function (h) {
      entries.push({ type: 'section', t: h.textContent.replace('¶', '').trim(), p: here + '#' + h.id, k: '§' });
    });

    var overlay = document.createElement('div');
    overlay.className = 'palette-overlay'; overlay.id = 'palette';
    overlay.innerHTML =
      '<div class="palette" role="dialog" aria-modal="true" aria-label="Search the wiki">' +
      '<input type="text" placeholder="Search pages and sections…" aria-label="Search" autocomplete="off" ' +
      'spellcheck="false" role="combobox" aria-expanded="true" aria-controls="palette-list" aria-autocomplete="list">' +
      '<div class="palette-list" id="palette-list" role="listbox" aria-label="Results"></div></div>';
    document.body.appendChild(overlay);
    var lastFocused = null;

    var input = overlay.querySelector('input');
    var list = overlay.querySelector('.palette-list');
    var active = 0, visible = [];

    function render(q) {
      q = (q || '').toLowerCase().trim();
      visible = entries.filter(function (e) { return !q || e.t.toLowerCase().indexOf(q) > -1; });
      if (!visible.length) { list.innerHTML = '<div class="palette-empty">No matches</div>'; return; }
      active = 0;
      list.innerHTML = visible.map(function (e, i) {
        return '<a class="palette-item' + (i === 0 ? ' active' : '') + '" id="pal-' + i + '" role="option" ' +
          'aria-selected="' + (i === 0 ? 'true' : 'false') + '" href="' + e.p + '" data-i="' + i + '">' +
          '<span class="pk">' + (e.type === 'section' ? '§' : e.k) + '</span>' +
          '<span class="pt">' + e.t + '</span>' +
          '<span class="pp">' + (e.type === 'section' ? 'section' : 'page') + '</span></a>';
      }).join('');
      Array.prototype.forEach.call(list.children, function (c) {
        c.addEventListener('mousemove', function () { setActive(+c.getAttribute('data-i')); });
      });
      input.setAttribute('aria-activedescendant', visible.length ? 'pal-0' : '');
    }
    function setActive(i) {
      active = i;
      Array.prototype.forEach.call(list.children, function (c, j) {
        var on = j === i;
        c.classList.toggle('active', on);
        c.setAttribute('aria-selected', on ? 'true' : 'false');
      });
      input.setAttribute('aria-activedescendant', 'pal-' + i);
    }
    function open() {
      lastFocused = document.activeElement;
      overlay.classList.add('open'); input.value = ''; render(''); input.focus();
    }
    function close() {
      overlay.classList.remove('open');
      if (lastFocused && lastFocused.focus) lastFocused.focus();
    }

    input.addEventListener('input', function () { render(input.value); });
    overlay.addEventListener('click', function (e) { if (e.target === overlay) close(); });
    overlay.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { close(); }
      else if (e.key === 'ArrowDown') { e.preventDefault(); setActive(Math.min(active + 1, visible.length - 1)); list.children[active].scrollIntoView({ block: 'nearest' }); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(Math.max(active - 1, 0)); list.children[active].scrollIntoView({ block: 'nearest' }); }
      else if (e.key === 'Enter') { e.preventDefault(); if (visible[active]) location.href = visible[active].p; }
    });

    document.addEventListener('keydown', function (e) {
      var typing = /^(INPUT|TEXTAREA)$/.test(document.activeElement.tagName);
      if ((e.key === '/' && !typing) || ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k')) {
        e.preventDefault(); open();
      }
    });
    document.querySelectorAll('[data-open-palette]').forEach(function (b) { b.addEventListener('click', open); });
  }
})();
