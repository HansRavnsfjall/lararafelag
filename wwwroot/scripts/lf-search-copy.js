/* =========================================================
   SEARCH CONFIG (edit per site; can be moved to a .js file)
   ========================================================= */
   const SEARCH_API       = "/umbraco/api/search/query";   // your controller route
   const ALIAS            = "tidindaelement";              // main news/doc alias(es), comma-separated OK
   const SITE_ROOTS       = ["81f22605-925d-4848-bb38-b886783b2083"]; // GUID Keys
   const GROUP_ROOTS      = [ // Right-column/root buckets, in display order
     "0cb6d8be-f07f-48e3-8bdf-37194587c939",
     "1abfe2c9-5188-47ae-9750-1fb00f1ace21",
     "b3a451ee-3d08-40aa-bf70-c0925f318522"
   ];
   const RESULTS_PAGE_URL = "/leiting";                    // your results page URL
   const PAGE_SIZE        = 30;                            // results page page-size
   const OTHER_ALIAS      = "undirsida";                   // extra alias bucket
   const OTHER_LABEL      = "Aðrar síður";                 // display name for that bucket
   const DEBUG            = false;                         // set true to show request + JSON in <pre id="so-debug">
       


/* =======================================================================
   TYPEAHEAD OVERLAY (requires: .search-icon, #search-overlay, #so-input,
   #so-results — optional: .so-close, .so-backdrop, #so-debug)
   ======================================================================= */
   (function(){
    document.addEventListener('DOMContentLoaded', initTypeahead);
  
    function initTypeahead(){
      const overlay  = byId('search-overlay');
      const input    = byId('so-input');
      const results  = byId('so-results');
      const closeBtn = overlay ? q('.so-close', overlay) : null;
      const backdrop = overlay ? q('.so-backdrop', overlay) : null;
      const dbgEl    = byId('so-debug');
  
      // Open overlay via any .search-icon (supports multiple/dynamic)
      document.addEventListener('click', (e) => {
        const trigger = e.target.closest('.search-icon');
        if (trigger) { e.preventDefault(); openOverlay(); }
      });
  
      if (!overlay || !input || !results) return; // overlay not on this page
  
      if (closeBtn) closeBtn.addEventListener('click', closeOverlay);
      if (backdrop) backdrop.addEventListener('click', closeOverlay);
      document.addEventListener('keydown', (e) => {
        if (overlay.classList.contains('open') && e.key === 'Escape') closeOverlay();
      });
  
      // Minimal API for manual control
      window.SearchUI = { open: openOverlay, close: closeOverlay };
  
      let debounceId = null;
      let ac = null; // AbortController
  
      // Only search after typing (no initial fetch)
      input.addEventListener('input', () => {
        clearTimeout(debounceId);
        const term = input.value.trim();
  
        if (!term) { // nothing typed → clear
          if (ac) ac.abort();
          results.innerHTML = '';
          if (DEBUG && dbgEl) { dbgEl.style.display = 'block'; dbgEl.textContent = ''; }
          return;
        }
  
        showSkeleton(results);
        debounceId = setTimeout(() => doSearch(term), 180);
      });
  
      function openOverlay(){
        overlay.classList.add('open');
        document.body.classList.add('so-lock');
        overlay.setAttribute('aria-hidden','false');
        results.innerHTML = '';
        input.value = '';
        if (DEBUG && dbgEl) { dbgEl.style.display = 'block'; dbgEl.textContent = ''; }
        setTimeout(() => input.focus(), 0);
        // (no initial fetch; wait for typing)
      }
  
      function closeOverlay(){
        overlay.classList.remove('open');
        document.body.classList.remove('so-lock');
        overlay.setAttribute('aria-hidden','true');
        results.innerHTML = '';
        input.value = '';
        if (DEBUG && dbgEl) { dbgEl.style.display = 'none'; dbgEl.textContent = ''; }
        if (ac) ac.abort();
      }
  
      function showSkeleton(root){
        root.innerHTML = `<div class="so-skel"><div class="bar"></div><div class="bar"></div></div>`;
      }
  
      function buildTypeaheadUrl(term){
        const p = new URLSearchParams();
        p.set('q', term); p.set('Q', term); // support both cases
        p.set('alias', ALIAS);
        if (SITE_ROOTS?.length)  p.set('siteRoots', SITE_ROOTS.join(','));
        if (GROUP_ROOTS?.length) p.set('groupRoots', GROUP_ROOTS.join(','));
        // extra alias bucket (Aðrar síður)
        p.set('otherAlias', OTHER_ALIAS);
        p.set('otherLabel', OTHER_LABEL);
        p.set('takePerGroup', '3');
        p.set('mode', 'typeahead');
        return `${SEARCH_API}?${p.toString()}`;
      }
  
      async function doSearch(term){
        try{
          if (ac) ac.abort();
          ac = new AbortController();
          const url = buildTypeaheadUrl(term);
          const res = await fetch(url, { signal: ac.signal });
          if (DEBUG && dbgEl) dbgEl.textContent = `GET ${url}\n\n`;
          if (!res.ok){
            results.innerHTML = `<div class="so-empty">Feilur: ${res.status} ${res.statusText}</div>`;
            return;
          }
          const data = await res.json();
          if (DEBUG && dbgEl) dbgEl.textContent += JSON.stringify(data, null, 2);
          paintTypeahead(results, data, term);
        } catch(err){
          if (err.name !== 'AbortError'){
            console.error('[Typeahead] fetch error:', err);
            results.innerHTML = `<div class="so-empty">Tókst ikki at leita: ${escapeHtml(err.message)}</div>`;
          }
        }
      }
  
      // Render only categories with results; if none → "ongi úrslit"
      function paintTypeahead(root, data, term){
        const groups = data?.groups ?? data?.Groups ?? [];
        if (!Array.isArray(groups)) { root.innerHTML = `<div class="so-empty">ongi úrslit</div>`; return; }
  
        let html = '';
        for (let i=0; i<groups.length; i++){
          const g        = groups[i] || {};
          const kind     = (g.kind ?? g.Kind ?? 'root').toLowerCase(); // "root" | "alias"
          const aliasK   = g.alias ?? g.Alias ?? null;
  
          const rootName = g.rootName ?? g.RootName ?? 'Bólkur';
          const total    = Number(g.total ?? g.Total ?? 0);
          const itemsArr = g.items ?? g.Items ?? [];
  
          if (!total || !itemsArr.length) continue; // skip empties
  
          // Link: alias bucket goes with ?primaryAlias=..., root buckets with ?root=<GUID>
          let catUrl;
          if (kind === 'alias' && aliasK) {
            const u = new URL(RESULTS_PAGE_URL, window.location.origin);
            if (term) u.searchParams.set('q', term);
            u.searchParams.set('primaryAlias', aliasK);
            catUrl = u.toString();
          } else {
            // map root-bucket order back to GROUP_ROOTS order (alias bucket is appended after these)
            const rootKey = GROUP_ROOTS[Math.min(i, GROUP_ROOTS.length-1)] || '';
            const u = new URL(RESULTS_PAGE_URL, window.location.origin);
            if (term) u.searchParams.set('q', term);
            u.searchParams.set('root', rootKey);
            catUrl = u.toString();
          }
  
          const items = itemsArr.slice(0,3).map(it => {
            const name = it.name ?? it.Name ?? '';
            const url  = it.url  ?? it.Url  ?? '#';
            return `<li><a href="${url}">${escapeHtml(name)}</a></li>`;
          }).join('');
  
          html += `
            <section class="so-group" style="padding:10px 6px 6px;border-bottom:1px solid #eee">
              <h3 style="margin:0 0 6px;font-size:15px;font-weight:600;display:flex;gap:10px;align-items:baseline">
                <a href="${catUrl}" style="text-decoration:none">${escapeHtml(rootName)}</a>
                <a href="${catUrl}" class="so-count" aria-label="Vís øll úrslit" style="text-decoration:none;opacity:.75">(${total})</a>
              </h3>
              <ul style="list-style:none;margin:0;padding-left:0;display:grid;gap:6px">${items}</ul>
            </section>
          `;
        }
  
        root.innerHTML = html || `<div class="so-empty">ongi úrslit</div>`;
      }
    }
  
    // helpers
    function q(sel, parent=document){ return parent ? parent.querySelector(sel) : null; }
    function byId(id){ return document.getElementById(id); }
    function escapeHtml(s){ return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
  })();
  
  /* ==============================================================================
     "LEITING" RESULTS PAGE (Bootstrap 5 UI expected)
     Reads: ?q=term & either ?root=<GUID> or ?primaryAlias=undirsida
     Left column = chosen bucket (root or alias). Right column = other buckets.
     ============================================================================== */
  (function(){
    document.addEventListener('DOMContentLoaded', initLeiting);
  
    function initLeiting(){
      const rootEl   = byId('leiting-root');
      if (!rootEl) return; // not on results page
  
      const input    = byId('leiting-q');
      const form     = byId('leiting-form');
      const left     = byId('leiting-left');
      const right    = byId('leiting-right');
      const pager    = byId('leiting-pager');
      const summary  = byId('leiting-summary');
      const leftLoad = byId('leiting-left-loading');
      const rightLoad= byId('leiting-right-loading');
      const resultsUrl = rootEl?.dataset?.resultsUrl || RESULTS_PAGE_URL;
  
      const url = new URL(window.location.href);
      let q           = url.searchParams.get('q') || "";
      let rootKey     = url.searchParams.get('root') || GROUP_ROOTS[0];
      let primaryAlias= url.searchParams.get('primaryAlias'); // if present, take precedence
      let page        = parseInt(url.searchParams.get('page') || "1", 10);
  
      if (!GROUP_ROOTS.includes(rootKey)) rootKey = GROUP_ROOTS[0];
      if (input) input.value = q;
  
      // Sidebar root set:
      // - if primaryAlias is set → show all roots in sidebar
      // - else → show all roots except the current primary root
      let otherRoots = primaryAlias ? [...GROUP_ROOTS] : GROUP_ROOTS.filter(k => k !== rootKey);
  
      if (form) form.addEventListener('submit', (e) => {
        e.preventDefault();
        q = (input?.value || '').trim();
        page = 1;
        updateUrl();
        load();
      });
  
      function updateUrl(){
        const u = new URL(window.location.href);
        if (q) u.searchParams.set('q', q); else u.searchParams.delete('q');
        if (primaryAlias) {
          u.searchParams.set('primaryAlias', primaryAlias);
          u.searchParams.delete('root');
        } else {
          u.searchParams.set('root', rootKey);
          u.searchParams.delete('primaryAlias');
        }
        u.searchParams.set('page', page);
        history.replaceState(null, "", u.toString());
      }
  
      function buildApiUrl(){
        const p = new URLSearchParams();
        if (q) p.set('q', q);
        p.set('alias', ALIAS);
        if (SITE_ROOTS.length) p.set('siteRoots', SITE_ROOTS.join(','));
  
        if (primaryAlias) {
          p.set('primaryAlias', primaryAlias); // left column is the alias bucket
        } else {
          p.set('primaryRoot', rootKey);       // left column is the chosen root
        }
  
        if (otherRoots.length) p.set('groupRoots', otherRoots.join(','));
        // Always request the extra alias group (appended last)
        p.set('otherAlias', OTHER_ALIAS);
        p.set('otherLabel', OTHER_LABEL);
  
        p.set('skip', ((page-1)*PAGE_SIZE));
        p.set('take', PAGE_SIZE);
        p.set('takePerGroup', 3);
        p.set('mode', 'full');
        return `${SEARCH_API}?${p.toString()}`;
      }
  
      async function load(){
        if (leftLoad)  leftLoad.style.display = '';
        if (rightLoad) rightLoad.style.display = '';
        left.innerHTML = '';
        right.innerHTML = '';
        pager.innerHTML = '';
        const res = await fetch(buildApiUrl());
        if (leftLoad)  leftLoad.style.display = 'none';
        if (rightLoad) rightLoad.style.display = 'none';
  
        if (!res.ok){
          left.innerHTML = `<div class="alert alert-danger">Tókst ikki at lesa úrslit (${res.status}).</div>`;
          return;
        }
        const data = await res.json();
        renderAll(data);
      }
  
      function renderAll(data){
        const primary   = data?.primary ?? data?.Primary ?? null;
        const pItems    = primary?.items ?? primary?.Items ?? [];
        const pName     = primary?.rootName ?? primary?.RootName ?? '';
        const pTotal    = Number(primary?.total ?? primary?.Total ?? 0);
  
        renderSummary(q, pName, pTotal);
  
        if (!primary || !pItems.length){
          left.innerHTML = `<div class="alert alert-info mb-0">ongi úrslit</div>`;
          pager.innerHTML = '';
        } else {
          const list = pItems.map(it => `
            <a class="list-group-item list-group-item-action" href="${it.url ?? it.Url}">
              ${escapeHtml(it.name ?? it.Name ?? '')}
            </a>
          `).join('');
          left.innerHTML = `
            <div class="d-flex align-items-center justify-content-between mb-2">
              <h2 class="h5 mb-0">${escapeHtml(pName)} <span class="badge text-bg-secondary">${pTotal}</span></h2>
            </div>
            <div class="list-group">${list}</div>
          `;
          renderPager(pTotal);
        }
  
        // Right sidebar: render only non-empty groups.
        // We also skip the alias group if it's the current primaryAlias (no duplicate).
        const groups = data?.groups ?? data?.Groups ?? [];
        const pieces = [];
        let rootIdx = 0; // track mapping into otherRoots for kind='root'
  
        for (let i = 0; i < groups.length; i++){
          const g        = groups[i] || {};
          const kind     = (g.kind ?? g.Kind ?? 'root').toLowerCase(); // 'root' | 'alias'
          const aliasK   = g.alias ?? g.Alias ?? null;
  
          const gName    = g.rootName ?? g.RootName ?? 'Bólkur';
          const gTotal   = Number(g.total ?? g.Total ?? 0);
          const gItems   = g.items ?? g.Items ?? [];
  
          if (!gTotal || !gItems.length) continue; // hide empties
  
          // Skip alias bucket if it's the current left column
          if (primaryAlias && kind === 'alias' && aliasK && aliasK.toLowerCase() === primaryAlias.toLowerCase()) {
            continue;
          }
  
          // Decide target link
          let link;
          if (kind === 'alias' && aliasK) {
            link = buildCatUrl(null, aliasK); // will set ?primaryAlias=
          } else {
            const targetRootKey = otherRoots[rootIdx] || otherRoots[otherRoots.length - 1] || GROUP_ROOTS[0];
            link = buildCatUrl(targetRootKey, null); // will set ?root=
            rootIdx++;
          }
  
          // Preview (top 3)
          const mini = gItems.slice(0,3).map(it =>
            `<li class="list-group-item py-2">
              <a class="stretched-link text-decoration-none" href="${it.url ?? it.Url}">
                ${escapeHtml(it.name ?? it.Name ?? '')}
              </a>
            </li>`
          ).join('');
  
          pieces.push(`
            <div class="list-group-item">
              <div class="d-flex align-items-center justify-content-between">
                <a class="fw-semibold text-decoration-none" href="${link}">${escapeHtml(gName)}</a>
                <a class="badge rounded-pill text-bg-secondary text-decoration-none" href="${link}" aria-label="Vís øll úrslit">${gTotal}</a>
              </div>
            </div>
            <ul class="list-group list-group-flush">${mini}</ul>
          `);
        }
  
        right.innerHTML = pieces.join('') || `<div class="list-group-item text-muted">Ongar aðrar bólkar við úrslitum.</div>`;
      }
  
      function renderSummary(term, catName, total){
        const bits = [];
        if (term) bits.push(`"${escapeHtml(term)}"`);
        if (catName) bits.push(`${escapeHtml(catName)} (${total})`);
        (byId('leiting-summary') || {}).innerHTML = bits.join(' · ');
      }
  
      function renderPager(total){
        const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
        pager.innerHTML = '';
        if (totalPages <= 1) return;
  
        const add = (label, p, disabled=false, active=false) => `
          <li class="page-item ${disabled?'disabled':''} ${active?'active':''}">
            <a class="page-link" href="#" data-page="${p}">${label}</a>
          </li>`;
  
        pager.insertAdjacentHTML('beforeend', add('←', page-1, page<=1));
        const windowSize = 5;
        const start = Math.max(1, page - Math.floor(windowSize/2));
        const end   = Math.min(totalPages, start + windowSize - 1);
        for (let p = start; p <= end; p++) pager.insertAdjacentHTML('beforeend', add(String(p), p, false, p===page));
        pager.insertAdjacentHTML('beforeend', add('→', page+1, page>=totalPages));
  
        pager.querySelectorAll('a.page-link').forEach(a => {
          a.addEventListener('click', (e) => {
            e.preventDefault();
            const next = parseInt(a.dataset.page, 10);
            if (!next || next === page || next < 1 || next > totalPages) return;
            page = next;
            updateUrl();
            load();
          });
        });
      }
  
      function buildCatUrl(targetRootKey, aliasKey){
        const u = new URL(resultsUrl, window.location.origin);
        if (q) u.searchParams.set('q', q);
        if (aliasKey) {
          u.searchParams.set('primaryAlias', aliasKey);
          u.searchParams.delete('root');
        } else {
          u.searchParams.set('root', targetRootKey);
          u.searchParams.delete('primaryAlias');
        }
        u.searchParams.set('page', 1);
        return u.toString();
      }
  
      // init
      updateUrl();
      load();
    }
  
    // helpers
    function byId(id){ return document.getElementById(id); }
    function q(sel, parent=document){ return parent ? parent.querySelector(sel) : null; }
    function escapeHtml(s){ return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
  })();
  