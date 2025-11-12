/* Lærarafelagið — Search (TYPEAHEAD ONLY)
   - Uses /umbraco/api/search/query
   - Groups by your fixed roots + alias bucket (“Aðrar síður”)
   - Navigates to /leiting on category clicks; reloads if URL identical
*/

(function(){
  // ===== CONFIG (edit per site) =====
  const SEARCH_API  = "/umbraco/api/search/query";
  const ALIAS       = "tidindaelement";
  const SITE_ROOTS  = ["535f7cdc-9c1e-49b9-a115-f79d553b9984"];
  const GROUP_ROOTS = [
    "c5dd03ef-7fd5-4b58-9ac0-65e11f2487d6"
  
  ];
  const OTHER_ALIAS = "undirsida";
  const OTHER_LABEL = "Aðrar síður";

  const RESULTS_PAGE_URL = "/leiting-2";
  const PAGE_SIZE        = 30; // used only to prefill otherTake for results links
  const TAKE_PER_GROUP   = 3;  // preview per group in typeahead
  const MIN_CHARS        = 1;
  const DEBOUNCE_MS      = 220;

  // ===== HELPERS =====
  const byId = (id) => document.getElementById(id);
  const escapeHtml = (s) => String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const skeleton = () => `
    <div class="lfso__skeleton">
      ${Array.from({length:3}).map(()=>`
        <div class="lfso__group">
          <div class="lfso__ghead"><div class="bar bar-sm"></div></div>
          <div class="lfso__gbody">
            <div class="bar"></div>
            <div class="bar"></div>
            <div class="bar bar-short"></div>
          </div>
        </div>
      `).join('')}
    </div>
  `;
  function debounce(fn, ms){ let t; return (...args)=>{ clearTimeout(t); t=setTimeout(()=>fn(...args), ms); }; }

  function normalizeUrl(u){
    const x = new URL(u, window.location.origin);
    const entries = Array.from(x.searchParams.entries()).sort(([a],[b]) => a.localeCompare(b));
    x.search = "";
    for (const [k,v] of entries) x.searchParams.append(k,v);
    return x.toString();
  }
  function forceNavigate(href){
    const target = normalizeUrl(href);
    const current = normalizeUrl(window.location.href);
    if (target === current) window.location.reload(); else window.location.assign(target);
  }

  // =======================================================
  // TYPEAHEAD OVERLAY
  // Needs: .search-icon, #search-overlay, #so-input, #so-results
  // Optional: [data-close], .lfso__backdrop, #so-submit
  // =======================================================
  document.addEventListener("DOMContentLoaded", () => {
    const overlay   = byId("search-overlay");
    const resultsEl = byId("so-results");
    const input     = byId("so-input");
    const submitBtn = byId("so-submit");
    const resultsUrl= overlay?.dataset?.resultsUrl || RESULTS_PAGE_URL;

    if (!overlay || !resultsEl || !input) return;

    let aborter = null;
    let lastTerm = "";
    let previousFocus = null;

    // Expose optional open/close handles
    window.LFSearch = { open: openOverlay, close: closeOverlay };

    // Open triggers
    document.addEventListener('click', (e) => {
      const opener = e.target.closest('.search-icon, [data-action="open-search"], .js-open-search');
      if (opener) { e.preventDefault(); openOverlay(); }
    });

    // Close triggers
    overlay.addEventListener('click', (e) => {
      if (e.target.closest('[data-close]') || e.target.classList.contains('lfso__backdrop')) {
        e.preventDefault();
        closeOverlay();
      }
    });
    document.addEventListener('keydown', (e) => {
      if (overlay.classList.contains('open') && e.key === 'Escape') closeOverlay();
    });

    // Input + Search
    input.addEventListener('input', debounce(runSearch, DEBOUNCE_MS));
    submitBtn && submitBtn.addEventListener('click', gotoResultsAll);
    overlay.querySelector('form')?.addEventListener('submit', (e)=> e.preventDefault());

    function openOverlay(){
      previousFocus = document.activeElement;
      overlay.classList.add('open');
      overlay.setAttribute('aria-hidden','false');
      document.body.classList.add('so-lock');
      requestAnimationFrame(() => { try { input.focus({preventScroll:true}); input.select(); } catch(_){} });
      if ((input.value || "").trim().length >= MIN_CHARS) runSearch();
      else resultsEl.innerHTML = "";
    }
    function closeOverlay(){
      overlay.classList.remove('open');
      overlay.setAttribute('aria-hidden','true');
      document.body.classList.remove('so-lock');
      previousFocus?.focus?.();
    }

    async function runSearch(){
      const term = input.value.trim();
      if (term === lastTerm) return;
      lastTerm = term;

      if (term.length < MIN_CHARS){ resultsEl.innerHTML = ""; return; }

      resultsEl.setAttribute('aria-busy','true');
      resultsEl.innerHTML = skeleton();

      aborter?.abort();
      aborter = new AbortController();

      try{
        const res = await fetch(buildTypeaheadUrl(term), { signal: aborter.signal, headers: {'Accept':'application/json'} });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        renderTypeahead(term, data, resultsUrl);
      }catch(err){
        if (err.name !== 'AbortError'){
          resultsEl.innerHTML = `<div class="lfso__empty">Tókst ikki at leita (${escapeHtml(err.message)}).</div>`;
        }
      }finally{
        resultsEl.setAttribute('aria-busy','false');
      }
    }

    function buildTypeaheadUrl(term){
      const p = new URLSearchParams();
      p.set('q', term);
      p.set('alias', ALIAS);
      if (SITE_ROOTS.length)  p.set('siteRoots', SITE_ROOTS.join(','));
      if (GROUP_ROOTS.length) p.set('groupRoots', GROUP_ROOTS.join(','));
      p.set('otherAlias', OTHER_ALIAS);
      p.set('otherLabel', OTHER_LABEL);
      p.set('takePerGroup', String(TAKE_PER_GROUP));
      p.set('take', '0');          // preview only
      p.set('mode', 'typeahead');
      return `${SEARCH_API}?${p.toString()}`;
    }

    function renderTypeahead(term, data, resultsUrl){
      let groups = data?.groups ?? data?.Groups ?? [];
      groups = groups.filter(g => Number(g.total ?? g.Total ?? 0) > 0);

      if (!groups.length){
        resultsEl.innerHTML = `<div class="lfso__empty">Ongi úrslit.</div>`;
        return;
      }

      resultsEl.innerHTML = groups.map(g => {
        const kind   = String(g.kind ?? g.Kind ?? 'root').toLowerCase(); // root | alias
        const alias  = g.alias ?? g.Alias ?? null;                       // "undirsida" for aðrar síður
        const gKey   = g.rootKey ?? g.RootKey ?? '';
        const gName  = g.rootName ?? g.RootName ?? 'Bólkur';
        const gTotal = Number(g.total ?? g.Total ?? 0);
        const gItems = (g.items ?? g.Items ?? []).slice(0, TAKE_PER_GROUP);

        // Build category link to full results page
        const catHref = (() => {
          const u = new URL(resultsUrl, window.location.origin);
          if (term) u.searchParams.set('q', term);
          if (kind === 'alias' && alias){
            u.searchParams.set('primaryAlias', alias);
            u.searchParams.set('otherSkip', '0');
            u.searchParams.set('otherTake', String(PAGE_SIZE));
            u.searchParams.delete('root');
          } else if (gKey){
            u.searchParams.set('root', gKey);
            u.searchParams.delete('primaryAlias');
          }
          u.searchParams.set('page','1');
          return u.toString();
        })();

        const list = gItems.map(it => {
          const url = it.url ?? it.Url ?? '#';
          const name = it.name ?? it.Name ?? '';
          return `<li><a href="${url}">${escapeHtml(name)}</a></li>`;
        }).join('');

        const moreLink = (gTotal > TAKE_PER_GROUP)
          ? `<a class="lfso__more" data-force-nav="1" href="${catHref}">
               Vís øll úrslit í ${escapeHtml(gName)} <span aria-hidden="true">→</span>
             </a>`
          : '';

        return `
          <section class="lfso__group">
            <div class="lfso__ghead">
              <h3 class="lfso__gtitle"><a data-force-nav="1" href="${catHref}">${escapeHtml(gName)}</a></h3>
              <a class="lfso__count" data-force-nav="1" href="${catHref}">(${gTotal})</a>
            </div>
            <div class="lfso__gbody">
              <ul class="lfso__list">${list}</ul>
              ${moreLink}
            </div>
          </section>
        `;
      }).join('');

      // Force navigation for our category links even if other scripts intercept
      resultsEl.addEventListener('click', (e) => {
        const a = e.target.closest('a[data-force-nav]');
        if (!a) return;
        e.preventDefault();
        e.stopPropagation();
        e.stopImmediatePropagation();
        forceNavigate(a.href);
      }, { once:true, capture:true });
    }

    function gotoResultsAll(){
      const term = input.value.trim();
      const u = new URL(resultsUrl, window.location.origin);
      if (term) u.searchParams.set('q', term);
      u.searchParams.set('page', '1');
      forceNavigate(u.toString());
    }
  });
})();
