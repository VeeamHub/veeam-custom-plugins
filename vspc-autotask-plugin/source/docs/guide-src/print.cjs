// Renders an HTML file to PDF through the DevTools protocol, so that a footer template
// (page numbers) and a document outline can be supplied -- the Chrome CLI has neither flag.
//
//   node --experimental-websocket print.cjs [input.html] [output.pdf]
const fs = require('fs');
const path = require('path');

const DIR = __dirname;
const PORT = process.env.CDP_PORT || 9222;
const IN = path.join(DIR, process.argv[2] || 'guide.html');
const OUT = path.join(DIR, process.argv[3] || 'Autotask-PSA-Integration-Guide.pdf');
const URL = 'file:///' + IN.replace(/\\/g, '/');

const footer = `
<div style="width:100%;font-family:'Open Sans','Segoe UI',Arial,sans-serif;font-size:7.5pt;color:#8a9198;
     padding:0 20mm;display:flex;justify-content:space-between;align-items:center;">
  <span>Autotask PSA Integration for Veeam Service Provider Console</span>
  <span><span class="pageNumber"></span></span>
</div>`;

const header = '<div style="display:none"></div>';

async function main() {
  const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
  const target = list.find(t => t.type === 'page');
  if (!target) throw new Error('no page target found');

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  let id = 0;
  const pending = new Map();
  const events = new Map();

  const send = (method, params = {}) => new Promise((res, rej) => {
    const msgId = ++id;
    pending.set(msgId, { res, rej });
    ws.send(JSON.stringify({ id: msgId, method, params }));
  });
  const once = name => new Promise(res => events.set(name, res));

  ws.addEventListener('message', ev => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) {
      const { res, rej } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.error ? rej(new Error(msg.error.message)) : res(msg.result);
    } else if (msg.method && events.has(msg.method)) {
      events.get(msg.method)();
      events.delete(msg.method);
    }
  });

  await new Promise(res => ws.addEventListener('open', res));

  await send('Page.enable');
  await send('Emulation.setEmulatedMedia', { media: 'print' });
  await send('Page.navigate', { url: 'about:blank' });
  await new Promise(r => setTimeout(r, 300));
  const loaded = once('Page.loadEventFired');
  await send('Page.navigate', { url: URL });
  await loaded;
  // Open Sans is fetched from Google Fonts; wait for webfonts to settle before printing.
  await send('Runtime.evaluate', { expression: 'document.fonts.ready', awaitPromise: true });
  await new Promise(r => setTimeout(r, 1200));

  const { data } = await send('Page.printToPDF', {
    printBackground: true,
    preferCSSPageSize: true,
    displayHeaderFooter: true,
    headerTemplate: header,
    footerTemplate: footer,
    generateDocumentOutline: true,
  });

  fs.writeFileSync(OUT, Buffer.from(data, 'base64'));
  console.log('written ' + path.basename(OUT) + ' (' + fs.statSync(OUT).size + ' bytes)');
  ws.close();
}

main().catch(e => { console.error('ERROR: ' + e.message); process.exit(1); });
