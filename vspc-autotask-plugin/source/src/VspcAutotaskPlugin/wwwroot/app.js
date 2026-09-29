"use strict";

// ---------------------------------------------------------------- utilities

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

// Served through the VSPC portal, the app lives under /plugins/{pluginId}/ and all API
// calls are proxied through that prefix (empty prefix when hit directly on the plugin port).
const API_BASE = (() => {
  const match = location.pathname.match(/^(.*\/plugins\/[^/]+)(\/|$)/i);
  return match ? match[1] : "";
})();

// The VSPC proxy does not inject identity headers for plain SPA requests; like Veeam's
// UI toolkit, we present the signed-in portal user's own bearer token, discovered from
// web storage (the plugin window shares the VSPC portal's origin).
let VSPC_BEARER;

function findVspcBearer() {
  const jwtRe = /^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$/;
  const candidates = [];
  const collect = value => {
    if (typeof value === "string" && jwtRe.test(value.trim())) candidates.push(value.trim());
  };
  for (const store of [window.localStorage, window.sessionStorage]) {
    let length = 0;
    try { length = store.length; } catch { continue; }
    for (let i = 0; i < length; i++) {
      let raw;
      try { raw = store.getItem(store.key(i)); } catch { continue; }
      if (!raw || !raw.includes("eyJ")) continue;
      collect(raw);
      try {
        const stack = [JSON.parse(raw)];
        while (stack.length) {
          const cur = stack.pop();
          if (typeof cur === "string") collect(cur);
          else if (cur && typeof cur === "object") Object.values(cur).forEach(v => stack.push(v));
        }
      } catch { /* not JSON */ }
    }
  }
  // Prefer a live access token: valid exp with the shortest remaining lifetime
  // (refresh tokens are JWTs too but live much longer).
  const now = Date.now() / 1000;
  const scored = candidates.map(token => {
    try {
      const payload = JSON.parse(atob(token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")));
      return { token, exp: payload.exp || 0 };
    } catch { return { token, exp: 0 }; }
  }).filter(x => x.exp > now).sort((a, b) => a.exp - b.exp);
  return scored.length ? scored[0].token : "";
}

function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, ch => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
  }[ch]));
}

function toast(message, isError = false) {
  const el = document.createElement("div");
  el.className = "toast" + (isError ? " error" : "");
  el.textContent = message;
  $("#toasts").appendChild(el);
  setTimeout(() => el.remove(), isError ? 8000 : 4000);
}

function fmtTime(iso) {
  if (!iso) return "—";
  const d = new Date(iso);
  return isNaN(d) ? esc(iso) : d.toLocaleString();
}

async function api(path, options = {}) {
  const init = {
    method: options.method || (options.body ? "POST" : "GET"),
    headers: { "Content-Type": "application/json" },
    credentials: "same-origin"
  };
  if (VSPC_BEARER === undefined) VSPC_BEARER = findVspcBearer();
  // Custom header: the VSPC proxy rejects Authorization headers on plugin routes (403),
  // so the portal token travels under a name the proxy passes through untouched.
  if (VSPC_BEARER) init.headers["X-Vspc-Access-Token"] = VSPC_BEARER;
  if (options.body !== undefined) init.body = JSON.stringify(options.body);
  const res = await fetch(API_BASE + path, init);
  if (res.status === 401 || res.status === 403) {
    showDenied();
    throw new Error("Not authorized (" + res.status + ")");
  }
  let data = {};
  try { data = await res.json(); } catch { /* empty body */ }
  if (!res.ok) throw new Error(data.message || data.error || `${res.status} ${res.statusText}`);
  return data;
}

async function guarded(button, fn) {
  const original = button?.textContent;
  if (button) { button.disabled = true; button.textContent = "Working…"; }
  try { await fn(); }
  catch (err) { toast(err.message, true); }
  finally { if (button) { button.disabled = false; button.textContent = original; } }
}

// ------------------------------------------------------------------- session

// VSPC authenticates portal users and forwards X-User-Uid on proxied requests;
// a denied user simply lacks the required portal role.
function showDenied() {
  $("#shell").classList.add("hidden");
  $("#auth-overlay").classList.remove("hidden");
}

async function initSession() {
  let s = null;
  let lastError = null;
  try {
    s = await api("/api/session");
  } catch (err) { lastError = err; }

  if (!s || !s.authenticated) {
    // The portal token may have rotated (or been rejected) — rescan storage once.
    VSPC_BEARER = findVspcBearer();
    try {
      s = await api("/api/session");
      lastError = null;
    } catch (err) { lastError = err; s = null; }
  }

  if (s && s.authenticated) { boot(); return; }
  if (s) { showDenied(); return; } // service reachable; user simply not authorized

  // Service truly unreachable through the proxy: persistent explanation, not a blank page.
  $("#auth-hint").textContent =
    "The plugin service is not reachable through the VSPC proxy (" +
    (lastError ? lastError.message : "no response") + "). " +
    "Check the plugin's health on its Catalog tile, and the service logs in " +
    "%ProgramData%\\VspcAutotaskPlugin\\logs on the VSPC server.";
  showDenied();
}

// -------------------------------------------------------------------- router

const views = {};
let activityTimer = null;

function setView(name) {
  $$(".nav-btn").forEach(b => b.classList.toggle("active", b.dataset.view === name));
  clearInterval(activityTimer);
  activityTimer = null;
  views[name]().catch(err => toast(err.message, true));
}

function boot() {
  $("#auth-overlay").classList.add("hidden");
  $("#shell").classList.remove("hidden");
  $$(".nav-btn").forEach(b => b.addEventListener("click", () => setView(b.dataset.view)));
  setView("dashboard");
  maybeOfferSetup();
}

// First run inside VSPC: if Autotask has never been connected, open the
// configuration dialog automatically (CWM-style "Configure Plugin Connection").
async function maybeOfferSetup() {
  try {
    const s = await api("/api/status");
    if (!s.autotask.configured) openSetupWizard(true);
  } catch { /* the dashboard surfaces connection errors on its own */ }
}

// ------------------------------------------------- configuration wizard popup

function closeWizard() {
  $("#wizard-overlay")?.remove();
}

function wizardMark(step) {
  $$("#wizard-overlay .step-dot").forEach(d =>
    d.classList.toggle("active", Number(d.dataset.step) <= step));
}

function openSetupWizard(firstRun) {
  closeWizard();
  const overlay = document.createElement("div");
  overlay.id = "wizard-overlay";
  overlay.className = "wizard-overlay";
  overlay.innerHTML = `
    <div class="dialog">
      <div class="dialog-head">
        <h2>Autotask PSA — plugin configuration</h2>
        <button class="btn ghost small" id="wz-close" title="Close">✕</button>
      </div>
      <div class="steps">
        <span class="step-dot active" data-step="1">1 · Connect to Autotask</span>
        <span class="step-dot" data-step="2">2 · Enable features</span>
        <span class="step-dot" data-step="3">3 · Done</span>
      </div>
      <div class="dialog-body" id="wz-body"></div>
    </div>`;
  document.body.appendChild(overlay);
  $("#wz-close").addEventListener("click", closeWizard);
  wizardStepConnect(firstRun).catch(err => toast(err.message, true));
}

async function wizardStepConnect(firstRun) {
  wizardMark(1);
  const existing = await api("/api/settings/autotask").catch(() => ({}));
  $("#wz-body").innerHTML = `
    <p class="muted">${firstRun
      ? "Welcome! Connect the plugin to your Autotask instance to get started."
      : "Update or replace the Autotask connection for this plugin."}</p>
    <div class="form-grid">
      <label>API user (email)<input id="wz-user" value="${esc(existing.username ?? "")}" placeholder="api-user@example.com"></label>
      <label>Secret<input id="wz-secret" type="password" placeholder="${existing.hasSecret ? "(unchanged)" : ""}"></label>
      <label>Integration code<input id="wz-code" value="${esc(existing.integrationCode ?? "")}" placeholder="tracking identifier"></label>
    </div>
    <p class="hint">Create an API-only user in Autotask (security level "API User (system)") with a
      Custom (Internal Integration) tracking identifier. The Autotask zone is detected automatically.</p>
    <div id="wz-health" class="health hidden"></div>
    <div class="dialog-actions">
      ${existing.zoneUrl ? '<button class="btn danger" id="wz-disconnect">Disconnect</button>' : ""}
      <span style="flex:1"></span>
      <button class="btn" id="wz-skip">Configure later</button>
      <button class="btn primary" id="wz-connect">Connect</button>
    </div>`;

  $("#wz-skip").addEventListener("click", closeWizard);
  $("#wz-disconnect")?.addEventListener("click", ev => guarded(ev.target, async () => {
    if (!confirm("Disconnect from Autotask? Billing and ticketing stop until reconnected.")) return;
    await api("/api/settings/autotask/disconnect", { body: {} });
    toast("Autotask disconnected");
    closeWizard();
    setView("dashboard");
  }));
  $("#wz-connect").addEventListener("click", ev => guarded(ev.target, async () => {
    const health = $("#wz-health");
    health.className = "health hidden";
    try {
      const r = await api("/api/settings/autotask", {
        body: {
          username: $("#wz-user").value.trim(),
          secret: $("#wz-secret").value || null,
          integrationCode: $("#wz-code").value.trim()
        }
      });
      health.className = "health ok";
      health.textContent = "✓ Healthy integration status — " + r.message;
      setTimeout(() => wizardStepFeatures().catch(err => toast(err.message, true)), 900);
    } catch (err) {
      health.className = "health err";
      health.textContent = "✕ " + err.message;
    }
  }));
}

async function wizardStepFeatures() {
  wizardMark(2);
  const features = await api("/api/settings/features");
  $("#wz-body").innerHTML = `
    <p class="muted">Choose which integration features to enable — they can be changed later
      under Settings.</p>
    <div class="feature-list">
      <label class="check"><input type="checkbox" id="wz-f-companies" ${features.companies ? "checked" : ""}>
        <span><strong>Companies</strong><br><span class="muted">Map VSPC companies to Autotask companies</span></span></label>
      <label class="check"><input type="checkbox" id="wz-f-billing" ${features.billing ? "checked" : ""}>
        <span><strong>Billing</strong><br><span class="muted">Sync measured usage into Autotask recurring-service contracts</span></span></label>
      <label class="check"><input type="checkbox" id="wz-f-ticketing" ${features.ticketing ? "checked" : ""}>
        <span><strong>Ticketing</strong><br><span class="muted">Create Autotask service tickets from VSPC alarms</span></span></label>
    </div>
    <div class="dialog-actions">
      <button class="btn" id="wz-all">Enable all</button>
      <span style="flex:1"></span>
      <button class="btn primary" id="wz-save">Save & finish</button>
    </div>`;

  $("#wz-all").addEventListener("click", () =>
    ["#wz-f-companies", "#wz-f-billing", "#wz-f-ticketing"].forEach(id => { $(id).checked = true; }));
  $("#wz-save").addEventListener("click", ev => guarded(ev.target, async () => {
    await api("/api/settings/features", {
      body: {
        companies: $("#wz-f-companies").checked,
        billing: $("#wz-f-billing").checked,
        ticketing: $("#wz-f-ticketing").checked
      }
    });
    wizardStepDone();
  }));
}

function wizardStepDone() {
  wizardMark(3);
  $("#wz-body").innerHTML = `
    <div class="health ok" style="margin-top:4px">✓ The Autotask PSA integration is configured.</div>
    <p class="muted" style="margin-top:12px">Next steps: map companies on the <strong>Companies</strong> tab,
      set up service &amp; contract mappings under <strong>Billing</strong>, and pick which alarms create
      tickets on the <strong>Ticketing</strong> tab.</p>
    <div class="dialog-actions">
      <span style="flex:1"></span>
      <button class="btn primary" id="wz-done">Go to dashboard</button>
    </div>`;
  $("#wz-done").addEventListener("click", () => { closeWizard(); setView("dashboard"); });
}

// ----------------------------------------------------------------- dashboard

views.dashboard = async function () {
  const s = await api("/api/status");
  const conn = (ok) => ok ? '<span class="badge ok">Connected</span>' : '<span class="badge err">Not configured</span>';
  const feat = (on) => on ? '<span class="badge ok">On</span>' : '<span class="badge">Off</span>';
  const poll = s.lastTicketPoll;
  $("#view").innerHTML = `
    <h1>Dashboard</h1>
    <p class="muted">Autotask PSA integration for Veeam Service Provider Console — company mapping, billing and automated ticketing.</p>
    <div class="cards">
      <div class="card"><div class="muted">Veeam Service Provider Console</div>
        <div class="big">${conn(s.vspc.configured)}</div>
        <div class="hint mono">${esc(s.vspc.baseUrl || "awaiting auto-provisioning by VSPC")}</div></div>
      <div class="card"><div class="muted">Autotask PSA</div>
        <div class="big">${conn(s.autotask.configured)}</div>
        <div class="hint mono">${esc(s.autotask.zoneUrl || "not set")}</div></div>
      <div class="card"><div class="muted">Features</div>
        <div class="row" style="margin-top:8px">Companies ${feat(s.features.companies)}</div>
        <div class="row">Billing ${feat(s.features.billing)}</div>
        <div class="row">Ticketing ${feat(s.features.ticketing)}</div></div>
      <div class="card"><div class="muted">Mapped companies</div><div class="big">${s.counts.mappedCompanies}</div>
        <div class="hint">${s.counts.vspcCompanies} VSPC / ${s.counts.atCompanies} Autotask cached</div></div>
      <div class="card"><div class="muted">Open tickets</div><div class="big">${s.counts.openTickets}</div>
        <div class="hint">${s.counts.pendingTickets} pending in delay window</div></div>
      <div class="card"><div class="muted">Billing-enabled companies</div><div class="big">${s.counts.billingCompanies}</div>
        <div class="hint">Last run: ${fmtTime(s.lastBillingRun)}</div></div>
    </div>
    <div class="panel">
      <h2>Ticket engine</h2>
      <p class="muted">${s.counts.enabledAlarms} alarm template(s) enabled for ticketing.</p>
      ${poll ? `<p>Last poll ${fmtTime(poll.at)} — swept ${poll.swept} alarm event(s):
        ${poll.created} created, ${poll.closed} closed, ${poll.notes} notes, ${poll.cancelled} cancelled, ${poll.errors} errors.
        ${poll.message ? `<span class="error">${esc(poll.message)}</span>` : ""}</p>` : `<p class="muted">No polls yet.</p>`}
      <div class="row">
        <button class="btn" id="poll-now">Poll alarms now</button>
        <button class="btn" id="refresh-companies">Refresh company cache</button>
        <button class="btn" id="open-wizard">Configure Autotask connection…</button>
      </div>
    </div>`;
  $("#poll-now").addEventListener("click", ev => guarded(ev.target, async () => {
    const r = await api("/api/ticketing/poll", { body: {} });
    toast(`Poll complete: ${r.created} created, ${r.closed} closed, ${r.errors} errors`);
    setView("dashboard");
  }));
  $("#refresh-companies").addEventListener("click", ev => guarded(ev.target, async () => {
    const r = await api("/api/companies/refresh", { body: {} });
    toast(`Company cache refreshed (${r.vspc} VSPC, ${r.autotask} Autotask)`);
    setView("dashboard");
  }));
  $("#open-wizard").addEventListener("click", () => openSetupWizard(false));
};

// ----------------------------------------------------------------- companies

views.companies = async function () {
  const data = await api("/api/companies");
  if (data.needsRefresh) {
    $("#view").innerHTML = `
      <h1>Companies</h1>
      <div class="panel"><p>Company lists have not been loaded yet.</p>
      <button class="btn primary" id="first-refresh">Load companies from VSPC and Autotask</button></div>`;
    $("#first-refresh").addEventListener("click", ev =>
      guarded(ev.target, async () => { await api("/api/companies/refresh", { body: {} }); setView("companies"); }));
    return;
  }

  const unmappedAt = data.atCompanies.filter(c => !c.mapped);
  const atOptions = unmappedAt.map(c => `<option value="${c.id}">${esc(c.name)} (#${c.id})</option>`).join("");
  const rows = data.vspcCompanies.map(c => {
    const mapCell = c.mapping
      ? `<span class="badge ok">${esc(c.mapping.atName)} (#${c.mapping.atId})</span> ${c.mapping.auto ? '<span class="badge info">auto</span>' : ""}`
      : `<select data-uid="${esc(c.uid)}" class="map-select"><option value="">— select Autotask company —</option>${atOptions}</select>`;
    const action = c.mapping
      ? `<button class="btn small danger unmap-btn" data-uid="${esc(c.uid)}">Unmap</button>`
      : `<button class="btn small map-btn" data-uid="${esc(c.uid)}">Map</button>`;
    return `<tr>
      <td>${esc(c.name)}</td>
      <td><span class="badge ${c.status === "Active" ? "ok" : ""}">${esc(c.status ?? "?")}</span></td>
      <td>${mapCell}</td><td>${action}</td></tr>`;
  }).join("");

  $("#view").innerHTML = `
    <h1>Companies</h1>
    <p class="muted">Map Veeam Service Provider Console companies to Autotask companies. Mapping enables billing and ticketing for the company.</p>
    <div class="row" style="margin:12px 0">
      <button class="btn" id="refresh-btn">Refresh lists</button>
      <button class="btn" id="automap-preview">Preview auto-map</button>
      <button class="btn primary" id="automap-apply">Auto-map exact matches</button>
      <span class="muted">Cache from ${fmtTime(data.fetchedAt)} · ${unmappedAt.length} unmapped Autotask companies</span>
    </div>
    <div id="suggestions" class="suggestions"></div>
    <div class="panel table-wrap"><table>
      <thead><tr><th>VSPC company</th><th>Status</th><th>Autotask company</th><th></th></tr></thead>
      <tbody>${rows || '<tr><td colspan="4" class="muted">No VSPC companies found</td></tr>'}</tbody>
    </table></div>`;

  $("#refresh-btn").addEventListener("click", ev =>
    guarded(ev.target, async () => { await api("/api/companies/refresh", { body: {} }); setView("companies"); }));

  $("#automap-apply").addEventListener("click", ev => guarded(ev.target, async () => {
    const r = await api("/api/companies/automap", { body: { apply: true } });
    toast(`Auto-mapped ${r.autoMapped} companies; ${r.suggestions.length} need review`);
    setView("companies");
  }));

  $("#automap-preview").addEventListener("click", ev => guarded(ev.target, async () => {
    const r = await api("/api/companies/automap", { body: { apply: false } });
    const html = r.suggestions.length === 0
      ? "<p class='muted'>No match suggestions — everything mappable is either mapped or has no close name match.</p>"
      : r.suggestions.map(s => `
        <div class="row" style="margin:6px 0">
          <strong>${esc(s.vspcCompanyName)}</strong> →
          <span class="pill-group">${s.candidates.map(c =>
            `<button class="btn small suggest-btn" data-uid="${esc(s.vspcCompanyUid)}" data-atid="${c.atCompanyId}">
              ${esc(c.atCompanyName)} (${Math.round(c.score * 100)}%)</button>`).join("")}
          </span></div>`).join("");
    $("#suggestions").innerHTML = `<div class="panel"><h2>Suggested matches — click to map</h2>${html}</div>`;
    $$(".suggest-btn").forEach(b => b.addEventListener("click", ev2 => guarded(ev2.target, async () => {
      await api("/api/companies/map", { body: { vspcUid: b.dataset.uid, atId: Number(b.dataset.atid) } });
      setView("companies");
    })));
  }));

  $$(".map-btn").forEach(b => b.addEventListener("click", ev => guarded(ev.target, async () => {
    const select = $(`.map-select[data-uid="${CSS.escape(b.dataset.uid)}"]`);
    if (!select.value) { toast("Pick an Autotask company first", true); return; }
    await api("/api/companies/map", { body: { vspcUid: b.dataset.uid, atId: Number(select.value) } });
    setView("companies");
  })));

  $$(".unmap-btn").forEach(b => b.addEventListener("click", ev => guarded(ev.target, async () => {
    if (!confirm("Remove this mapping? Billing configuration for the company will also be removed.")) return;
    await api("/api/companies/unmap", { body: { vspcUid: b.dataset.uid } });
    setView("companies");
  })));
};

// ------------------------------------------------------------------- billing

let atServicesCache = null;
let atBillingCodesCache = null;
let periodTypesCache = null;

views.billing = async function () {
  const [services, companies] = await Promise.all([
    api("/api/billing/services"),
    api("/api/billing/companies")
  ]);
  try {
    if (!atServicesCache) atServicesCache = await api("/api/autotask/services");
    if (!atBillingCodesCache) atBillingCodesCache = await api("/api/autotask/billingcodes");
    if (!periodTypesCache) periodTypesCache = (await api("/api/autotask/servicefields")).periodTypes;
  } catch (err) {
    toast("Could not load Autotask metadata: " + err.message, true);
    atServicesCache = atServicesCache || [];
    atBillingCodesCache = atBillingCodesCache || [];
    periodTypesCache = periodTypesCache || [];
  }

  const svcOptions = sel => ['<option value="">— select service —</option>',
    ...atServicesCache.map(s => `<option value="${s.id}" ${String(s.id) === String(sel) ? "selected" : ""}>${esc(s.name)}</option>`)].join("");
  const codeOptions = sel => ['<option value="">— billing code —</option>',
    ...atBillingCodesCache.map(c => `<option value="${c.id}" ${String(c.id) === String(sel) ? "selected" : ""}>${esc(c.name)}</option>`)].join("");
  const periodOptions = sel => ['<option value="">— period —</option>',
    ...periodTypesCache.map(p => `<option value="${esc(p.value)}" ${String(p.value) === String(sel) ? "selected" : ""}>${esc(p.label)}</option>`)].join("");

  let currentGroup = "";
  const serviceRows = services.map(s => {
    const groupRow = s.group !== currentGroup ? `<tr><td colspan="5"><h3>${esc(s.group)}</h3></td></tr>` : "";
    currentGroup = s.group;
    return `${groupRow}<tr data-key="${esc(s.key)}">
      <td>${esc(s.name)} <span class="muted">(${esc(s.unit)})</span></td>
      <td><select class="svc-mode">
        <option value="Skip" ${s.mode === "Skip" ? "selected" : ""}>Do not bill</option>
        <option value="Existing" ${s.mode === "Existing" ? "selected" : ""}>Existing Autotask service</option>
        <option value="CreateNew" ${s.mode === "CreateNew" ? "selected" : ""}>Create new service</option>
      </select></td>
      <td class="svc-existing" ${s.mode !== "Existing" ? 'style="display:none"' : ""}>
        <select class="svc-id">${svcOptions(s.atServiceId)}</select></td>
      <td class="svc-create" ${s.mode !== "CreateNew" ? 'style="display:none"' : ""}>
        <div class="row">
          <input class="svc-name" placeholder="Service name" value="${esc(s.atServiceName ?? "Veeam — " + s.name)}" style="width:190px">
          <input class="svc-price" type="number" step="0.01" min="0" placeholder="Unit price" value="${s.unitPrice ?? ""}" style="width:100px">
          <select class="svc-code" style="max-width:170px">${codeOptions(s.billingCodeId)}</select>
          <select class="svc-period" style="max-width:130px">${periodOptions(s.periodType)}</select>
          ${s.atServiceId ? `<span class="badge info">created #${s.atServiceId}</span>` : ""}
        </div></td>
      <td><button class="btn small svc-save">Save</button></td></tr>`;
  }).join("");

  const mappedServiceKeys = services.filter(s => s.mode !== "Skip");
  const companyRows = companies.map(c => `
    <tr data-uid="${esc(c.vspcUid)}" data-atid="${c.atCompanyId}">
      <td>${esc(c.name)}<div class="hint">${esc(c.atCompanyName)} (#${c.atCompanyId})</div></td>
      <td class="contract-cell">
        ${c.contractId
          ? `<span class="badge ok">${esc(c.contractName ?? "contract")} (#${c.contractId})</span>
             <button class="btn small load-contracts">Change</button>`
          : `<button class="btn small load-contracts">Select contract…</button>`}
      </td>
      <td><div class="pill-group">${mappedServiceKeys.map(s =>
        `<label class="check" style="font-size:12.5px"><input type="checkbox" class="cb-service" value="${esc(s.key)}"
          ${c.enabledServices.includes(s.key) ? "checked" : ""}>${esc(s.name)}</label>`).join("") ||
        '<span class="muted">map services above first</span>'}</div></td>
      <td class="row">
        <button class="btn small cb-save">Save</button>
        ${c.contractId ? '<button class="btn small danger cb-remove">Disable</button>' : ""}
      </td></tr>`).join("");

  $("#view").innerHTML = `
    <h1>Billing</h1>
    <p class="muted">Mirror of the ConnectWise Manage billing workflow: map VSPC services to Autotask services,
      pick a contract per company, and the nightly sync posts contract service adjustments for measured usage.</p>

    <div class="panel"><h2>1 · Service mapping <span class="muted">(VSPC services → Autotask services)</span></h2>
      <div class="table-wrap"><table>
        <thead><tr><th>VSPC service</th><th>Mode</th><th>Existing service</th><th>New service details</th><th></th></tr></thead>
        <tbody>${serviceRows}</tbody></table></div></div>

    <div class="panel"><h2>2 · Company contracts & services <span class="muted">(≈ agreements & additions)</span></h2>
      <div class="table-wrap"><table>
        <thead><tr><th>Company</th><th>Autotask contract</th><th>Billed services</th><th></th></tr></thead>
        <tbody>${companyRows || '<tr><td colspan="4" class="muted">Map companies first</td></tr>'}</tbody></table></div></div>

    <div class="panel"><h2>3 · Synchronization</h2>
      <div class="row">
        <button class="btn" id="preview-btn">Preview (dry run)</button>
        <button class="btn primary" id="run-btn">Sync billing now</button>
        <button class="btn ghost" id="history-btn">Show history</button>
      </div>
      <div id="billing-results"></div></div>`;

  // --- service mapping handlers ---
  $$("tr[data-key]").forEach(row => {
    const modeSel = $(".svc-mode", row);
    modeSel.addEventListener("change", () => {
      $(".svc-existing", row).style.display = modeSel.value === "Existing" ? "" : "none";
      $(".svc-create", row).style.display = modeSel.value === "CreateNew" ? "" : "none";
    });
    $(".svc-save", row).addEventListener("click", ev => guarded(ev.target, async () => {
      const mode = modeSel.value;
      const body = { serviceKey: row.dataset.key, mode };
      if (mode === "Existing") {
        body.atServiceId = Number($(".svc-id", row).value) || null;
        if (!body.atServiceId) { toast("Select an Autotask service", true); return; }
      } else if (mode === "CreateNew") {
        body.atServiceName = $(".svc-name", row).value.trim();
        body.unitPrice = parseFloat($(".svc-price", row).value) || 0;
        body.billingCodeId = Number($(".svc-code", row).value) || null;
        body.periodType = Number($(".svc-period", row).value) || null;
      }
      await api("/api/billing/services", { body });
      toast("Service mapping saved");
    }));
  });

  // --- company billing handlers ---
  $$("tr[data-uid]").forEach(row => {
    $(".load-contracts", row)?.addEventListener("click", ev => guarded(ev.target, async () => {
      const contracts = await api("/api/autotask/contracts?companyId=" + row.dataset.atid);
      const cell = $(".contract-cell", row);
      cell.innerHTML = contracts.length === 0
        ? '<span class="error">No contracts found for this company in Autotask</span>'
        : `<select class="contract-select">${contracts.map(c =>
            `<option value="${c.id}" data-name="${esc(c.name)}">${esc(c.name)} (#${c.id})</option>`).join("")}</select>`;
    }));
    $(".cb-save", row).addEventListener("click", ev => guarded(ev.target, async () => {
      const select = $(".contract-select", row);
      const badge = $(".contract-cell .badge", row);
      let contractId, contractName;
      if (select) {
        contractId = Number(select.value);
        contractName = select.selectedOptions[0]?.dataset.name;
      } else if (badge) {
        const m = badge.textContent.match(/\(#(\d+)\)/);
        contractId = m ? Number(m[1]) : null;
        contractName = badge.textContent.replace(/\s*\(#\d+\)\s*$/, "");
      }
      if (!contractId) { toast("Select a contract first", true); return; }
      const enabledServices = $$(".cb-service:checked", row).map(cb => cb.value);
      await api("/api/billing/companies", { body: { vspcUid: row.dataset.uid, contractId, contractName, enabledServices } });
      toast("Company billing saved");
      setView("billing");
    }));
    $(".cb-remove", row)?.addEventListener("click", ev => guarded(ev.target, async () => {
      if (!confirm("Disable billing for this company?")) return;
      await api("/api/billing/companies/remove", { body: { vspcUid: row.dataset.uid } });
      setView("billing");
    }));
  });

  // --- sync handlers ---
  const renderResults = results => {
    const badge = st => ({ adjusted: "ok", planned: "info", "no-change": "", skipped: "warn", error: "err" }[st] ?? "");
    $("#billing-results").innerHTML = `
      <div class="table-wrap" style="margin-top:12px"><table>
        <thead><tr><th>Company</th><th>Service</th><th>Current</th><th>Measured</th><th>Δ</th><th>Status</th><th>Detail</th></tr></thead>
        <tbody>${results.map(r => `<tr>
          <td>${esc(r.vspcCompanyName)}</td><td>${esc(r.serviceName)}</td>
          <td>${r.currentUnits}</td><td>${r.desiredUnits}</td><td>${r.delta > 0 ? "+" + r.delta : r.delta}</td>
          <td><span class="badge ${badge(r.status)}">${esc(r.status)}</span></td>
          <td class="muted">${esc(r.detail ?? "")}</td></tr>`).join("") ||
          '<tr><td colspan="7" class="muted">Nothing to do — configure companies above</td></tr>'}
        </tbody></table></div>`;
  };
  $("#preview-btn").addEventListener("click", ev =>
    guarded(ev.target, async () => renderResults(await api("/api/billing/preview", { body: {} }))));
  $("#run-btn").addEventListener("click", ev => guarded(ev.target, async () => {
    if (!confirm("Run billing sync now? This posts contract service adjustments to Autotask.")) return;
    renderResults(await api("/api/billing/run", { body: {} }));
  }));
  $("#history-btn").addEventListener("click", ev => guarded(ev.target, async () => {
    const history = await api("/api/billing/history");
    $("#billing-results").innerHTML = `
      <div class="table-wrap" style="margin-top:12px"><table>
        <thead><tr><th>Time</th><th>Company</th><th>Service</th><th>Units</th><th>Δ</th><th>Dry run</th><th>Result</th></tr></thead>
        <tbody>${history.map(h => `<tr>
          <td>${fmtTime(h.ts)}</td><td>${esc(h.vspcCompanyName ?? h.vspcCompanyUid)}</td><td>${esc(h.serviceKey)}</td>
          <td>${h.prevUnits ?? "?"} → ${h.newUnits ?? "?"}</td><td>${h.delta ?? ""}</td>
          <td>${h.dryRun ? "yes" : "no"}</td><td class="muted">${esc(h.result ?? "")}</td></tr>`).join("") ||
          '<tr><td colspan="7" class="muted">No history yet</td></tr>'}
        </tbody></table></div>`;
  }));
};

// ----------------------------------------------------------------- ticketing

views.ticketing = async function () {
  const [alarms, links] = await Promise.all([
    api("/api/ticketing/alarms"),
    api("/api/ticketing/links?limit=100")
  ]);

  let currentCategory = "";
  const alarmRows = alarms.map(a => {
    const catRow = a.category !== currentCategory
      ? `<tr class="cat-row"><td colspan="3"><h3>${esc(a.category ?? "Other")}</h3></td></tr>` : "";
    currentCategory = a.category;
    return `${catRow}<tr class="alarm-row" data-name="${esc((a.name ?? "").toLowerCase())}">
      <td><input type="checkbox" class="cb-alarm" value="${esc(a.uid)}"></td>
      <td>${esc(a.name)}</td>
      <td>${a.enabled ? '<span class="badge ok">Tickets on</span>' : '<span class="badge">Off</span>'}</td></tr>`;
  }).join("");

  const stateBadge = s => ({ Open: "ok", Pending: "info", Closed: "", Cancelled: "warn", Error: "err" }[s] ?? "");
  const linkRows = links.map(l => `<tr>
    <td>${fmtTime(l.updatedAt)}</td><td>${esc(l.company ?? "?")}</td><td>${esc(l.alarm)}</td>
    <td>${esc(l.objectName ?? "")}</td>
    <td>${l.ticketNumber ? esc(l.ticketNumber) : l.ticketId ? "#" + l.ticketId : "—"}</td>
    <td><span class="badge ${stateBadge(l.state)}">${esc(l.state)}</span></td>
    <td class="muted">${esc(l.error ?? l.lastStatus ?? "")}</td></tr>`).join("");

  $("#view").innerHTML = `
    <h1>Ticketing</h1>
    <p class="muted">Select which VSPC alarms create Autotask service tickets. Configure queue, priorities and statuses
      under <a href="#" id="goto-settings">Settings</a>. Cloud Gateway, Internal, Plugin, Site and User alarms are excluded.</p>
    <div class="panel">
      <div class="row spread">
        <h2>Alarm selection</h2>
        <div class="row">
          <input id="alarm-filter" placeholder="Filter alarms…" style="width:220px">
          <button class="btn" id="alarm-refresh">Refresh from VSPC</button>
          <button class="btn primary" id="alarm-enable">Enable tickets</button>
          <button class="btn" id="alarm-disable">Disable</button>
        </div>
      </div>
      <div class="table-wrap" style="max-height:420px;overflow-y:auto"><table>
        <thead><tr><th style="width:36px"><input type="checkbox" id="cb-all"></th><th>Alarm</th><th>Status</th></tr></thead>
        <tbody id="alarm-body">${alarmRows || '<tr><td colspan="3" class="muted">No alarm templates cached — click "Refresh from VSPC"</td></tr>'}</tbody>
      </table></div></div>
    <div class="panel">
      <div class="row spread"><h2>Recent ticket activity</h2>
        <button class="btn" id="poll-btn">Poll alarms now</button></div>
      <div class="table-wrap"><table>
        <thead><tr><th>Updated</th><th>Company</th><th>Alarm</th><th>Object</th><th>Ticket</th><th>State</th><th>Info</th></tr></thead>
        <tbody>${linkRows || '<tr><td colspan="7" class="muted">No ticket activity yet</td></tr>'}</tbody>
      </table></div></div>`;

  $("#goto-settings").addEventListener("click", ev => { ev.preventDefault(); setView("settings"); });
  $("#cb-all").addEventListener("change", ev =>
    $$(".alarm-row:not([style*='display: none']) .cb-alarm").forEach(cb => cb.checked = ev.target.checked));
  $("#alarm-filter").addEventListener("input", ev => {
    const q = ev.target.value.toLowerCase();
    $$(".alarm-row").forEach(r => r.style.display = r.dataset.name.includes(q) ? "" : "none");
  });
  $("#alarm-refresh").addEventListener("click", ev => guarded(ev.target, async () => {
    const r = await api("/api/ticketing/alarms/refresh", { body: {} });
    toast(`Loaded ${r.count} alarm templates`);
    setView("ticketing");
  }));
  const setEnabled = enabled => async () => {
    const uids = $$(".cb-alarm:checked").map(cb => cb.value);
    if (uids.length === 0) { toast("Select at least one alarm", true); return; }
    await api("/api/ticketing/alarms/enable", { body: { uids, enabled } });
    setView("ticketing");
  };
  $("#alarm-enable").addEventListener("click", ev => guarded(ev.target, setEnabled(true)));
  $("#alarm-disable").addEventListener("click", ev => guarded(ev.target, setEnabled(false)));
  $("#poll-btn").addEventListener("click", ev => guarded(ev.target, async () => {
    const r = await api("/api/ticketing/poll", { body: {} });
    toast(r.message || `Poll complete: ${r.created} created, ${r.closed} closed, ${r.notes} notes, ${r.errors} errors`);
    setView("ticketing");
  }));
};

// ------------------------------------------------------------------ settings

views.settings = async function () {
  const [autotask, features, ticketing, billing] = await Promise.all([
    api("/api/settings/autotask"), api("/api/settings/features"),
    api("/api/settings/ticketing"), api("/api/settings/billing")
  ]);

  $("#view").innerHTML = `
    <h1>Settings</h1>
    <p class="muted">The VSPC connection is provisioned automatically by the VSPC plugin host —
      its status is shown on the Dashboard. Configure the Autotask connection and behavior here.</p>

    <div class="panel"><h2>Autotask PSA</h2>
      <div class="form-grid">
        <label>API user (email)<input id="a-user" value="${esc(autotask.username)}" placeholder="api-user@example.com"></label>
        <label>Secret<input id="a-secret" type="password" placeholder="${autotask.hasSecret ? "(unchanged)" : ""}"></label>
        <label>Integration code<input id="a-code" value="${esc(autotask.integrationCode)}" placeholder="tracking identifier"></label>
        <label>Zone (auto-detected)<input value="${esc(autotask.zoneUrl)}" disabled></label>
      </div>
      <div class="row" style="margin-top:12px">
        <button class="btn primary" id="a-save">Test & save</button>
        <span id="a-result" class="muted"></span>
      </div>
      <p class="hint">Create an API-only user (security level "API User (system)") in Autotask with a
        Custom (Internal Integration) tracking identifier.</p></div>

    <div class="panel"><h2>Integration features</h2>
      <div class="row" style="gap:22px">
        <label class="check"><input type="checkbox" id="f-companies" ${features.companies ? "checked" : ""}>Companies</label>
        <label class="check"><input type="checkbox" id="f-billing" ${features.billing ? "checked" : ""}>Billing</label>
        <label class="check"><input type="checkbox" id="f-ticketing" ${features.ticketing ? "checked" : ""}>Ticketing</label>
        <button class="btn primary" id="f-save">Save features</button>
      </div></div>

    <div class="panel"><h2>Ticket settings</h2>
      <div class="row" style="margin-bottom:10px">
        <button class="btn" id="t-load">Load Autotask picklists</button>
        <span class="muted" id="t-load-state">Queue/status/priority options come from your Autotask instance.</span>
      </div>
      <div class="form-grid">
        <label>Queue (service board)<select id="t-queue" data-value="${ticketing.queueId ?? ""}"><option value="">—</option></select></label>
        <label>Status for new tickets<select id="t-newstatus" data-value="${ticketing.newStatusId ?? ""}"><option value="">—</option></select></label>
        <label>Status for closed tickets<select id="t-donestatus" data-value="${ticketing.completeStatusId ?? ""}"><option value="">—</option></select></label>
        <label>Priority — Warning alarms<select id="t-warnprio" data-value="${ticketing.warningPriorityId ?? ""}"><option value="">—</option></select></label>
        <label>Priority — Error alarms<select id="t-errprio" data-value="${ticketing.errorPriorityId ?? ""}"><option value="">—</option></select></label>
        <label>Source<select id="t-source" data-value="${ticketing.sourceId ?? ""}"><option value="">(default)</option></select></label>
        <label>Ticket type<select id="t-type" data-value="${ticketing.ticketTypeId ?? ""}"><option value="">(default)</option></select></label>
        <label>Delay before ticket (minutes)<input id="t-delay" type="number" min="0" max="1440" value="${ticketing.delayMinutes}"></label>
        <label>Due date offset (hours)<input id="t-due" type="number" min="1" max="720" value="${ticketing.dueHours}"></label>
        <label>Alarm poll interval (seconds)<input id="t-poll" type="number" min="30" max="3600" value="${ticketing.pollSeconds}"></label>
      </div>
      <div class="row" style="gap:22px;margin-top:6px">
        <label class="check"><input type="checkbox" id="t-close" ${ticketing.closeTicketOnAlarmResolve ? "checked" : ""}>Close ticket when alarm resolves</label>
        <label class="check"><input type="checkbox" id="t-resolve" ${ticketing.resolveAlarmOnTicketClose ? "checked" : ""}>Resolve alarm when ticket closes</label>
        <label class="check"><input type="checkbox" id="t-note" ${ticketing.noteOnRetrigger ? "checked" : ""}>Add note on re-trigger</label>
        <label class="check"><input type="checkbox" id="t-ack" ${ticketing.acknowledgeClosesTicket ? "checked" : ""}>Acknowledged counts as resolved</label>
      </div>
      <div class="row" style="margin-top:12px"><button class="btn primary" id="t-save">Save ticket settings</button></div></div>

    <div class="panel"><h2>Billing settings</h2>
      <div class="form-grid">
        <label>Billing period start day<input id="b-anchor" type="number" min="1" max="28" value="${billing.anchorDayOfMonth}"></label>
        <label>Daily sync hour (UTC)<input id="b-hour" type="number" min="0" max="23" value="${billing.syncHourUtc}"></label>
        <label>Default billing code (for created services)
          <select id="b-code" data-value="${billing.defaultBillingCodeId ?? ""}"><option value="">—</option></select></label>
        <label>Default period type (for created services)
          <select id="b-period" data-value="${billing.defaultPeriodType ?? ""}"><option value="">—</option></select></label>
      </div>
      <div class="row" style="margin-top:12px">
        <button class="btn" id="b-load">Load Autotask options</button>
        <button class="btn primary" id="b-save">Save billing settings</button>
      </div></div>`;

  // --- Autotask handlers ---
  $("#a-save").addEventListener("click", ev => guarded(ev.target, async () => {
    const r = await api("/api/settings/autotask", {
      body: {
        username: $("#a-user").value.trim(),
        secret: $("#a-secret").value || null,
        integrationCode: $("#a-code").value.trim()
      }
    });
    $("#a-result").textContent = r.message;
    toast("Autotask connection saved");
  }));

  // --- features ---
  $("#f-save").addEventListener("click", ev => guarded(ev.target, async () => {
    await api("/api/settings/features", {
      body: {
        companies: $("#f-companies").checked,
        billing: $("#f-billing").checked,
        ticketing: $("#f-ticketing").checked
      }
    });
    toast("Features saved");
  }));

  // --- ticket picklists ---
  const fillSelect = (sel, options, keepBlank) => {
    const current = sel.dataset.value;
    sel.innerHTML = (keepBlank ? sel.innerHTML : "") +
      options.map(o => `<option value="${esc(o.value)}">${esc(o.label)}</option>`).join("");
    if (current) sel.value = current;
  };
  $("#t-load").addEventListener("click", ev => guarded(ev.target, async () => {
    const p = await api("/api/autotask/picklists");
    fillSelect($("#t-queue"), p.queues, true);
    fillSelect($("#t-newstatus"), p.statuses, true);
    fillSelect($("#t-donestatus"), p.statuses, true);
    fillSelect($("#t-warnprio"), p.priorities, true);
    fillSelect($("#t-errprio"), p.priorities, true);
    fillSelect($("#t-source"), p.sources, true);
    fillSelect($("#t-type"), p.ticketTypes, true);
    $("#t-load-state").textContent = "Picklists loaded from Autotask.";
  }));
  $("#t-save").addEventListener("click", ev => guarded(ev.target, async () => {
    const num = id => { const v = $(id).value; return v === "" ? null : Number(v); };
    await api("/api/settings/ticketing", {
      body: {
        queueId: num("#t-queue"),
        newStatusId: num("#t-newstatus"),
        completeStatusId: num("#t-donestatus"),
        warningPriorityId: num("#t-warnprio"),
        errorPriorityId: num("#t-errprio"),
        sourceId: num("#t-source"),
        ticketTypeId: num("#t-type"),
        delayMinutes: Number($("#t-delay").value) || 0,
        dueHours: Number($("#t-due").value) || 24,
        pollSeconds: Number($("#t-poll").value) || 120,
        closeTicketOnAlarmResolve: $("#t-close").checked,
        resolveAlarmOnTicketClose: $("#t-resolve").checked,
        noteOnRetrigger: $("#t-note").checked,
        acknowledgeClosesTicket: $("#t-ack").checked
      }
    });
    toast("Ticket settings saved");
  }));

  // --- billing settings ---
  $("#b-load").addEventListener("click", ev => guarded(ev.target, async () => {
    const codes = await api("/api/autotask/billingcodes");
    const periods = (await api("/api/autotask/servicefields")).periodTypes;
    fillSelect($("#b-code"), codes.map(c => ({ value: c.id, label: c.name })), true);
    fillSelect($("#b-period"), periods, true);
  }));
  $("#b-save").addEventListener("click", ev => guarded(ev.target, async () => {
    await api("/api/settings/billing", {
      body: {
        anchorDayOfMonth: Number($("#b-anchor").value) || 1,
        syncHourUtc: Number($("#b-hour").value) || 1,
        defaultBillingCodeId: $("#b-code").value ? Number($("#b-code").value) : null,
        defaultPeriodType: $("#b-period").value ? Number($("#b-period").value) : null
      }
    });
    toast("Billing settings saved");
  }));
};

// ------------------------------------------------------------------ activity

views.activity = async function () {
  const render = async () => {
    const area = $("#act-area")?.value ?? "";
    const level = $("#act-level")?.value ?? "";
    const entries = await api(`/api/activity?limit=300${area ? "&area=" + area : ""}${level ? "&level=" + level : ""}`);
    const badge = lvl => ({ info: "info", warn: "warn", error: "err" }[lvl] ?? "");
    $("#act-body").innerHTML = entries.map(e => `<tr>
      <td class="mono">${fmtTime(e.ts)}</td>
      <td><span class="badge ${badge(e.level)}">${esc(e.level)}</span></td>
      <td>${esc(e.area)}</td>
      <td>${esc(e.message)}${e.detail ? `<div class="hint">${esc(e.detail)}</div>` : ""}</td></tr>`).join("") ||
      '<tr><td colspan="4" class="muted">No activity yet</td></tr>';
  };

  $("#view").innerHTML = `
    <h1>Activity</h1>
    <div class="row" style="margin:12px 0">
      <select id="act-area"><option value="">All areas</option>
        <option>connection</option><option>companies</option><option>billing</option>
        <option>ticketing</option><option>system</option></select>
      <select id="act-level"><option value="">All levels</option>
        <option>info</option><option>warn</option><option>error</option></select>
      <button class="btn" id="act-refresh">Refresh</button>
      <span class="muted">Auto-refreshes every 30 s while open</span>
    </div>
    <div class="panel table-wrap"><table>
      <thead><tr><th>Time</th><th>Level</th><th>Area</th><th>Message</th></tr></thead>
      <tbody id="act-body"></tbody></table></div>`;

  $("#act-area").addEventListener("change", render);
  $("#act-level").addEventListener("change", render);
  $("#act-refresh").addEventListener("click", render);
  await render();
  activityTimer = setInterval(render, 30000);
};

// --------------------------------------------------------------------- start

initSession();
