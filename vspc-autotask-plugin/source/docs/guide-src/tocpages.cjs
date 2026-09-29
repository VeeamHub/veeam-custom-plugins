// Reads the PDF outline that Chrome generates from the document headings, resolves each
// bookmark to a printed page number, and writes those numbers into the contents page.
//
//   node tocpages.cjs <pass1.pdf> <guide.html>
//
// Chrome emits a multi-level page tree and a nested outline tree, so both are walked
// properly rather than pattern-matched flat. Matching is by heading text, counting repeats
// in document order, so headings that recur (e.g. "Prerequisites") resolve correctly.
const fs = require('fs');

const pdfPath = process.argv[2];
const htmlPath = process.argv[3];
const raw = fs.readFileSync(pdfPath, 'latin1');

// ---- index every indirect object --------------------------------------------
const objects = new Map();
for (const m of raw.matchAll(/(\d+)\s+0\s+obj\b/g)) {
  const num = Number(m[1]);
  const start = m.index + m[0].length;
  const end = raw.indexOf('endobj', start);
  objects.set(num, raw.slice(start, end === -1 ? start + 4000 : end));
}
const obj = n => objects.get(n) || '';
const refIn = (body, key) => {
  const m = body.match(new RegExp('\\/' + key + '\\s+(\\d+)\\s+0\\s+R'));
  return m ? Number(m[1]) : null;
};

// ---- catalog ----------------------------------------------------------------
let catalog = null;
for (const [num, body] of objects) if (/\/Type\s*\/Catalog/.test(body)) { catalog = num; break; }
if (catalog === null) throw new Error('no /Catalog');
const rootPages = refIn(obj(catalog), 'Pages');
const rootOutline = refIn(obj(catalog), 'Outlines');
if (!rootPages) throw new Error('no /Pages in catalog');
if (!rootOutline) throw new Error('no /Outlines in catalog — was generateDocumentOutline set?');

// ---- walk the page tree in order -------------------------------------------
const pageOrder = [];
(function walkPages(num, depth) {
  if (depth > 32) return;
  const body = obj(num);
  if (/\/Type\s*\/Page[^s]/.test(body) || (!/\/Kids/.test(body) && /\/Contents/.test(body))) {
    pageOrder.push(num);
    return;
  }
  const kidsMatch = body.match(/\/Kids\s*\[([\s\S]*?)\]/);
  if (!kidsMatch) return;
  for (const k of kidsMatch[1].matchAll(/(\d+)\s+0\s+R/g)) walkPages(Number(k[1]), depth + 1);
})(rootPages, 0);
const pageIndex = new Map(pageOrder.map((n, i) => [n, i + 1]));

// ---- walk the outline tree in document order -------------------------------
const entries = [];
(function walkOutline(num, depth) {
  while (num && depth < 32) {
    const body = obj(num);
    const t = body.match(/\/Title\s*\(((?:\\.|[^)\\])*)\)/);
    const d = body.match(/\/Dest\s*\[\s*(\d+)\s+0\s+R/);
    if (t && d) {
      const page = pageIndex.get(Number(d[1]));
      if (page) entries.push({ title: t[1].replace(/\\([()\\])/g, '$1').trim(), page, depth });
    }
    const first = refIn(body, 'First');
    if (first) walkOutline(first, depth + 1);
    num = refIn(body, 'Next');
  }
})(refIn(obj(rootOutline), 'First'), 0);

// title -> pages, in document order
const byTitle = new Map();
for (const e of entries) {
  if (!byTitle.has(e.title)) byTitle.set(e.title, []);
  byTitle.get(e.title).push(e.page);
}

// ---- write the numbers into the contents page -------------------------------
let html = fs.readFileSync(htmlPath, 'utf8');
const seen = new Map();
const missing = [];
let filled = 0;

html = html.replace(/(<span class="pg" data-t=")([^"]*)("\s*>)([^<]*)(<\/span>)/g,
  (all, pre, title, mid, _old, post) => {
    const n = seen.get(title) || 0;
    seen.set(title, n + 1);
    const pages = byTitle.get(title);
    if (!pages || pages[n] === undefined) { missing.push(`${title} #${n + 1}`); return all; }
    filled++;
    return pre + title + mid + pages[n] + post;
  });

fs.writeFileSync(htmlPath, html);
console.log(`pdf pages: ${pageOrder.length}, outline entries: ${entries.length}, toc numbers filled: ${filled}`);
if (missing.length) console.log('UNRESOLVED: ' + missing.join(' | '));
else console.log('every contents entry resolved');
