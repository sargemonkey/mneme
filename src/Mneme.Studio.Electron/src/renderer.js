'use strict';

const $ = sel => document.querySelector(sel);
const eventsBox = $('#events');
const curationsBox = $('#curations');
const dlg = $('#dlg');
const dlgTitle = $('#dlgTitle');
const dlgBody = $('#dlgBody');
const dlgError = $('#dlgError');
const dlgOk = $('#dlgOk');
const dlgCancel = $('#dlgCancel');
const liveIndicator = $('#liveIndicator');

let currentWorkstream = null;
let dialogResolver = null;

function payloadSummary(json) {
    try {
        const o = JSON.parse(json);
        return o.content || o.statement || JSON.stringify(o).slice(0, 200);
    } catch { return json?.slice?.(0, 200) ?? ''; }
}

function ts(s) {
    if (!s) return '';
    try { return new Date(s).toISOString().replace('T', ' ').slice(0, 19) + 'Z'; }
    catch { return s; }
}

function renderEvents(events) {
    if (!events.length) {
        eventsBox.innerHTML = '<div class="empty">No events yet — chat with an MCP-aware agent that has the <code>remember</code> tool, or use <code>Mneme.Cli ingest …</code>.</div>';
        return;
    }
    eventsBox.innerHTML = events.map(e => `
        <div class="event ${e.is_revoked ? 'revoked' : ''}" data-id="${escapeAttr(e.event_id)}">
            <div class="event-header">
                <span class="badge cat-${e.category}">${e.category}</span>
                <span class="badge cls-${e.classification}">${e.classification}</span>
                <span class="event-id">${escapeHtml(e.event_id)}</span>
                <span class="event-time">${ts(e.created_at)}</span>
            </div>
            <div class="event-body">${escapeHtml(payloadSummary(e.payload_json))}</div>
            <details>
                <summary style="font-size: 10px; color: #8b96a8; cursor: pointer;">payload</summary>
                <div class="event-payload">${escapeHtml(e.payload_json)}</div>
            </details>
            ${e.is_revoked ? '' : `
                <div class="actions">
                    <button data-act="annotate">annotate</button>
                    <button data-act="pin" class="pin">pin</button>
                    <button data-act="demote" class="demote">demote</button>
                    <button data-act="amend">amend</button>
                    <button data-act="revoke" class="danger">revoke</button>
                </div>
            `}
        </div>
    `).join('');
}

function renderCurations(curations) {
    if (!curations.length) {
        curationsBox.innerHTML = '<div class="empty" style="padding: 20px;">No curations yet.</div>';
        return;
    }
    curationsBox.innerHTML = curations.map(c => `
        <div class="curation ${c.is_reverted ? 'reverted' : ''}" data-id="${escapeAttr(c.curation_event_id)}">
            <div><strong>${c.type}</strong> <span class="meta">by ${escapeHtml(c.curator)} · ${ts(c.occurred_at)}</span></div>
            <div class="meta">target: <span class="target">${escapeHtml(c.target_event_id)}</span></div>
            <div>${escapeHtml(c.rationale ?? '')}</div>
            ${c.is_reverted || c.type === 'Reverted' ? '' : `
                <div class="actions"><button data-revert-id="${escapeAttr(c.curation_event_id)}" class="danger">revert</button></div>
            `}
        </div>
    `).join('');
}

async function refresh() {
    try {
        const [m, e, c] = await Promise.all([
            window.mneme.metrics(),
            window.mneme.events(200),
            window.mneme.curations(200),
        ]);
        $('#mEvents').textContent = m.events;
        $('#mRevoked').textContent = m.revoked;
        $('#mCurations').textContent = m.curations;
        renderEvents(e);
        renderCurations(c);
        flashLive();
    } catch (ex) {
        console.error(ex);
    }
}

let liveFlashTimer = null;
function flashLive() {
    liveIndicator.style.color = '#4ade80';
    if (liveFlashTimer) clearTimeout(liveFlashTimer);
    liveFlashTimer = setTimeout(() => { liveIndicator.style.color = '#8b96a8'; }, 800);
}

async function loadConfig() {
    const cfg = await window.mneme.config();
    currentWorkstream = cfg.workstream;
    $('#dbPath').textContent = cfg.sqlitePath;
    const list = await window.mneme.workstreams();
    const pick = $('#wsPick');
    pick.innerHTML = list.map(w => `<option ${w === currentWorkstream ? 'selected' : ''}>${escapeHtml(w)}</option>`).join('');
    pick.onchange = async () => {
        await window.mneme.setWorkstream(pick.value);
        currentWorkstream = pick.value;
        refresh();
    };
}

// --- Modals -----------------------------------------------------------------
function showDialog(title, fields) {
    return new Promise(resolve => {
        dialogResolver = resolve;
        dlgTitle.textContent = title;
        dlgBody.innerHTML = fields.map(f => `
            <label>${f.label}</label>
            ${f.kind === 'textarea'
                ? `<textarea data-field="${f.name}">${escapeHtml(f.value ?? '')}</textarea>`
                : `<input data-field="${f.name}" type="${f.kind || 'text'}" value="${escapeAttr(f.value ?? '')}" />`}
        `).join('');
        dlgError.innerHTML = '';
        dlg.showModal();
    });
}

function closeDialog(result) {
    if (dialogResolver) {
        const r = dialogResolver;
        dialogResolver = null;
        r(result);
    }
    dlg.close();
}

dlgCancel.onclick = () => closeDialog(null);
dlgOk.onclick = () => {
    const values = {};
    dlg.querySelectorAll('[data-field]').forEach(el => {
        values[el.dataset.field] = el.value;
    });
    closeDialog(values);
};

async function showCliError(message) {
    dlgError.innerHTML = `<div class="error">${escapeHtml(message)}</div>`;
}

// --- Action wiring ----------------------------------------------------------
eventsBox.addEventListener('click', async (ev) => {
    const btn = ev.target.closest('button[data-act]');
    if (!btn) return;
    const card = btn.closest('.event');
    const id = card.dataset.id;
    const act = btn.dataset.act;
    try {
        switch (act) {
            case 'annotate': {
                const v = await showDialog('Annotate event', [{ name: 'text', label: 'Annotation text', kind: 'textarea' }]);
                if (!v) return;
                await window.mneme.cli(['annotate', '--event-id', id, '--text', v.text]);
                break;
            }
            case 'pin': {
                const v = await showDialog('Pin event', [{ name: 'mult', label: 'Multiplier (>1.0)', value: '2.0' }]);
                if (!v) return;
                await window.mneme.cli(['pin', '--event-id', id, '--multiplier', v.mult]);
                break;
            }
            case 'demote': {
                const v = await showDialog('Demote event', [{ name: 'mult', label: 'Multiplier (0.0..1.0)', value: '0.3' }]);
                if (!v) return;
                await window.mneme.cli(['demote', '--event-id', id, '--multiplier', v.mult]);
                break;
            }
            case 'amend': {
                const v = await showDialog('Amend fact', [
                    { name: 'newContent', label: 'New content', kind: 'textarea' },
                    { name: 'rationale', label: 'Rationale' },
                ]);
                if (!v) return;
                await window.mneme.cli(['amend', '--event-id', id, '--new-content', v.newContent, '--rationale', v.rationale || 'amend via Studio']);
                break;
            }
            case 'revoke': {
                const v = await showDialog('Revoke event', [{ name: 'reason', label: 'Reason' }]);
                if (!v) return;
                await window.mneme.cli(['revoke', '--event-id', id, '--reason', v.reason]);
                break;
            }
        }
        await refresh();
    } catch (ex) {
        alert(`Error: ${ex.message}`);
    }
});

curationsBox.addEventListener('click', async (ev) => {
    const btn = ev.target.closest('button[data-revert-id]');
    if (!btn) return;
    const id = btn.dataset.revertId;
    const v = await showDialog('Revert curation', [{ name: 'reason', label: 'Reason' }]);
    if (!v) return;
    try {
        await window.mneme.cli(['revert', '--curation-event-id', id, '--reason', v.reason]);
        await refresh();
    } catch (ex) {
        alert(`Error: ${ex.message}`);
    }
});

$('#pickDbBtn').onclick = async () => {
    const newPath = await window.mneme.pickDatabase();
    if (newPath) {
        await loadConfig();
        await refresh();
    }
};

// Helpers
function escapeHtml(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
function escapeAttr(s) { return escapeHtml(s); }

// Live updates from main
window.mneme.onChange(() => refresh());

// First load
(async () => { await loadConfig(); await refresh(); })();
