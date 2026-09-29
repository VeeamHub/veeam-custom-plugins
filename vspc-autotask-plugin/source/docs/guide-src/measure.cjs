// Reports any SVG child whose bounding box falls outside its viewBox (i.e. gets clipped),
// and any element pair that visually overlaps.
const path = require('path');
const DIR = __dirname;
const URL = 'file:///' + path.join(DIR, 'guide.html').replace(/\\/g, '/');

const EXPR = `(() => {
  const out = [];
  document.querySelectorAll('svg').forEach((svg, si) => {
    const vb = svg.viewBox.baseVal;
    const boxes = [];
    svg.querySelectorAll('text, rect').forEach(el => {
      let b; try { b = el.getBBox(); } catch (e) { return; }
      const over = [];
      if (b.x < vb.x - 0.5) over.push('left ' + (vb.x - b.x).toFixed(1));
      if (b.y < vb.y - 0.5) over.push('top ' + (vb.y - b.y).toFixed(1));
      if (b.x + b.width > vb.x + vb.width + 0.5) over.push('right ' + (b.x + b.width - vb.width - vb.x).toFixed(1));
      if (b.y + b.height > vb.y + vb.height + 0.5) over.push('bottom ' + (b.y + b.height - vb.height - vb.y).toFixed(1));
      if (over.length) out.push('svg' + si + ' CLIPPED [' + over.join(', ') + '] <' + el.tagName + '> "' + (el.textContent || '').slice(0, 40) + '"');
      if (el.tagName === 'text') boxes.push({ b, t: (el.textContent || '').slice(0, 30) });
    });
    // text-on-text collisions
    for (let i = 0; i < boxes.length; i++) for (let j = i + 1; j < boxes.length; j++) {
      const a = boxes[i].b, c = boxes[j].b;
      if (a.x < c.x + c.width && c.x < a.x + a.width && a.y < c.y + c.height && c.y < a.y + a.height)
        out.push('svg' + si + ' TEXT OVERLAP "' + boxes[i].t + '" / "' + boxes[j].t + '"');
    }
  });
  return out.length ? out.join('\\n') : 'clean: no clipped or overlapping SVG elements';
})()`;

async function main() {
  const list = await (await fetch('http://127.0.0.1:9222/json/list')).json();
  const target = list.find(t => t.type === 'page');
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  let id = 0; const pending = new Map(); const events = new Map();
  const send = (m, p = {}) => new Promise((res, rej) => { const i = ++id; pending.set(i, { res, rej }); ws.send(JSON.stringify({ id: i, method: m, params: p })); });
  const once = n => new Promise(res => events.set(n, res));
  ws.addEventListener('message', ev => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) { const { res, rej } = pending.get(msg.id); pending.delete(msg.id); msg.error ? rej(new Error(msg.error.message)) : res(msg.result); }
    else if (msg.method && events.has(msg.method)) { events.get(msg.method)(); events.delete(msg.method); }
  });
  await new Promise(res => ws.addEventListener('open', res));
  await send('Page.enable'); await send('Runtime.enable');
  await send('Emulation.setEmulatedMedia', { media: 'print' });
  const loaded = once('Page.loadEventFired');
  await send('Page.navigate', { url: URL });
  await loaded;
  await new Promise(r => setTimeout(r, 1000));
  const { result } = await send('Runtime.evaluate', { expression: EXPR, returnByValue: true });
  console.log(result.value);
  ws.close();
}
main().catch(e => { console.error('ERROR: ' + e.message); process.exit(1); });
