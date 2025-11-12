// make a global handle you can abort later
window.tidinda = (() => {
    let controller = null;
    let stop = false;
  
    async function runAll({ parentId, base, chunk = 500, startAt = 0, imagesOnly = false }) {
      const m = document.cookie.match(/(?:^|;\s*)UMB-XSRF-TOKEN=([^;]+)/);
      if (!m) { console.error('Log in to /umbraco first'); return; }
      const xsrf = decodeURIComponent(m[1]);
      const sleep = ms => new Promise(r => setTimeout(r, ms));
      const totals = {
        processed:0, created:0, updated:0, failures:0,
        imagesAttempted:0, imageCreated:0, imageFailed:0, imagesSetOnNodes:0,
        datesParsed:0, datesFailed:0
      };
  
      stop = false;
      for (let skip = startAt, batch = 0; ; skip += chunk, batch++) {
        if (stop) { console.warn('⏹️ Import stopped by user'); break; }
  
        const url = `/umbraco/backoffice/Tools/TidindaImport/Run` +
          `?parentId=${parentId}&skip=${skip}&limit=${chunk}&updateExisting=true` +
          (imagesOnly ? `&fixImagesOnly=true` : ``) +
          `&baseImageUrl=${encodeURIComponent(base)}`;
  
        controller = new AbortController();
        let json, txt;
        try {
          const r = await fetch(url, { method:'POST', headers:{ 'X-UMB-XSRF-TOKEN': xsrf }, signal: controller.signal });
          txt = await r.text();
          json = JSON.parse(txt.replace(/^\)\]\}',?/, ''));
        } catch (e) {
          if (e.name === 'AbortError') { console.warn('⏹️ Fetch aborted'); break; }
          console.error('Batch fetch failed:', e);
          break;
        }
  
        for (const k in totals) totals[k] += (json[k] || 0);
        console.log(`Batch ${batch} @ skip=${skip}`, json);
  
        if (!json || json.processed === 0) {
          console.log('✅ Import complete. Totals:', totals);
          break;
        }
        await sleep(300);
      }
    }
  
    return {
      start({ parentId, base = 'https://www.lararafelag.fo', chunk = 500, startAt = 0, imagesOnly = false } = {}) {
        runAll({ parentId, base, chunk, startAt, imagesOnly });
      },
      abort() {
        stop = true;
        if (controller) controller.abort();
      }
    };
  })();
  
  // EXAMPLE: start full import (change parentId)
  tidinda.start({ parentId: 4927, base: 'https://www.lararafelag.fo', chunk: 500, startAt: 0 });
  
  // WHEN YOU WANT TO STOP:
  tidinda.abort();
  