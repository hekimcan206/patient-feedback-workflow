// Case-tracking panel: Tabulator grid + Chart.js, kept current by SignalR pushes.
// No personal data arrives over the hub; the detail dialog fetches comment/notes (audited) and, for case managers,
// a contact reveal that requires a stated purpose (audited).
(function () {
    "use strict";

    const antiforgery = document.querySelector("input[name='__RequestVerificationToken']").value;
    const dialog = document.getElementById("case-dialog");
    const detail = document.getElementById("case-detail");
    const canReveal = dialog.dataset.canReveal === "true";
    const showResolved = document.getElementById("show-resolved");
    const live = document.getElementById("live");

    const fmtTime = (iso) => new Date(iso).toLocaleString("en-GB", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit" });
    const dueIn = (iso) => {
        const minutes = Math.round((new Date(iso) - Date.now()) / 60000);
        if (minutes < 0) { return "overdue " + fmtDuration(-minutes); }
        return "in " + fmtDuration(minutes);
    };
    const fmtDuration = (m) => (m >= 120 ? Math.round(m / 60) + " h" : m + " min");
    const text = (s) => { const span = document.createElement("span"); span.textContent = s ?? ""; return span.innerHTML; };

    const grid = new Tabulator("#case-grid", {
        layout: "fitColumns",
        height: "520px",
        index: "caseId",
        placeholder: "No cases",
        initialSort: [{ column: "slaDueAt", dir: "asc" }],
        rowFormatter: (row) => {
            const d = row.getData();
            const el = row.getElement();
            el.classList.toggle("row-breached", d.slaBreached && d.status !== "RESOLVED");
            el.classList.toggle("row-resolved", d.status === "RESOLVED");
        },
        columns: [
            { title: "#", field: "caseId", width: 64, hozAlign: "right" },
            {
                title: "Severity", field: "severity", width: 100,
                sorter: (a, b) => ({ High: 3, Medium: 2, Low: 1 }[a] - { High: 3, Medium: 2, Low: 1 }[b]),
                formatter: (c) => `<span class="sev sev-${c.getValue().toLowerCase()}">${c.getValue()}</span>`,
            },
            { title: "Department", field: "department", headerFilter: "input", minWidth: 120 },
            { title: "Scores", field: "nps", width: 130, formatter: (c) => { const d = c.getRow().getData(); return `NPS ${d.nps ?? "–"} · ${d.csat ?? "–"}/5 · ${d.ces ?? "–"}/7`; } },
            { title: "Why", field: "reasons", minWidth: 200, formatter: (c) => text(c.getValue()) },
            { title: "Status", field: "status", width: 110, headerFilter: "list", headerFilterParams: { values: ["OPEN", "IN_PROGRESS", "RESOLVED"], clearable: true } },
            { title: "Owner", field: "assignedTo", width: 130, formatter: (c) => text(c.getValue() ?? "—") },
            {
                title: "SLA", field: "slaDueAt", width: 130,
                formatter: (c) => { const d = c.getRow().getData(); return d.status === "RESOLVED" ? "done" : `<span class="${d.slaBreached ? "overdue" : ""}">${dueIn(c.getValue())}</span>`; },
            },
        ],
    });
    grid.on("rowClick", (_e, row) => openCase(row.getData().caseId));

    const applyFilter = () => {
        if (showResolved.checked) { grid.clearFilter(); } else { grid.setFilter("status", "!=", "RESOLVED"); }
    };
    showResolved.addEventListener("change", applyFilter);

    const chart = new Chart(document.getElementById("nps-chart"), {
        type: "bar",
        data: { labels: [], datasets: [{ label: "NPS", data: [], backgroundColor: [] }] },
        options: {
            indexAxis: "y", animation: false, maintainAspectRatio: false,
            scales: { x: { min: -100, max: 100 } },
            plugins: { legend: { display: false }, tooltip: { callbacks: { label: (ctx) => `NPS ${ctx.parsed.x} (n=${ctx.dataset.n[ctx.dataIndex]})` } } },
        },
    });

    async function loadCases() {
        const response = await fetch("/cases/data", { headers: { Accept: "application/json" } });
        if (response.ok) { await grid.replaceData(await response.json()); applyFilter(); }
    }

    let statsTimer = null;
    function scheduleStats() { clearTimeout(statsTimer); statsTimer = setTimeout(loadStats, 750); }

    async function loadStats() {
        const response = await fetch("/cases/stats?days=30", { headers: { Accept: "application/json" } });
        if (!response.ok) { return; }
        const s = await response.json();
        const set = (id, v) => { document.getElementById(id).textContent = v; };
        set("kpi-nps", s.nps.value ?? "–");
        set("kpi-nps-n", `n=${s.nps.responses}`);
        set("kpi-csat", s.csat.value == null ? "–" : s.csat.value + "%");
        set("kpi-csat-n", `n=${s.csat.responses}`);
        set("kpi-ces", s.cesMean.value ?? "–");
        set("kpi-ces-n", s.cesTopBox.value == null ? "" : `${s.cesTopBox.value}% found it easy`);
        set("kpi-open", s.cases.open + s.cases.inProgress);
        set("kpi-progress", `${s.cases.inProgress} in progress`);
        set("kpi-breached", s.cases.breached);
        set("kpi-resolved", `${s.cases.resolvedToday} resolved today`);
        chart.data.labels = s.departments.map((d) => d.department);
        chart.data.datasets[0].data = s.departments.map((d) => d.nps.value);
        chart.data.datasets[0].n = s.departments.map((d) => d.nps.responses);
        chart.data.datasets[0].backgroundColor = s.departments.map((d) => (d.nps.value < 0 ? "#c0392b" : d.nps.value < 30 ? "#e1a32a" : "#2e8b57"));
        chart.update();
    }

    async function post(url, body) {
        const response = await fetch(url, {
            method: "POST",
            headers: { "RequestVerificationToken": antiforgery, "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
            body: new URLSearchParams(body ?? {}),
        });
        return response;
    }

    async function openCase(id) {
        const response = await fetch(`/cases/${id}`, { headers: { Accept: "application/json" } });
        if (!response.ok) { return; }
        const d = await response.json();
        const c = d.case;
        const events = d.events.map((e) => `<li><span class="muted">${fmtTime(e.at)}</span> <strong>${text(e.action)}</strong> · ${text(e.actor)}${e.note ? `<div class="note">${text(e.note)}</div>` : ""}</li>`).join("");
        const open = c.status !== "RESOLVED";
        detail.innerHTML = `
            <h2>Case #${c.caseId} <span class="sev sev-${c.severity.toLowerCase()}">${c.severity}</span></h2>
            <p class="muted">${text(c.department)} · opened ${fmtTime(c.openedAt)} · SLA ${open ? dueIn(c.slaDueAt) : "done"} · ${text(c.status)}</p>
            <p><strong>Why:</strong> ${text(c.reasons)}</p>
            <p><strong>Scores:</strong> NPS ${c.nps ?? "–"}/10 · CSAT ${c.csat ?? "–"}/5 · CES ${c.ces ?? "–"}/7</p>
            ${d.comment ? `<blockquote>${text(d.comment)}</blockquote>` : `<p class="muted">No comment${c.hasComment ? "" : " left"}.</p>`}
            <div class="contact" id="contact-box">
                <strong>Contact:</strong> <span class="muted">hidden</span>
                ${canReveal && d.contactAvailable ? `
                    <select id="reveal-purpose"><option value="call-back-patient">call back patient</option><option value="complaint-escalation">complaint escalation</option><option value="verify-identity">verify identity</option></select>
                    <button class="secondary" id="reveal-btn">Reveal (audited)</button>` : ""}
                ${d.contactAvailable ? "" : `<span class="muted"> · anonymised under the retention policy</span>`}
            </div>
            <ol class="timeline">${events}</ol>
            ${open ? `
            <div class="actions">
                <button class="secondary" id="assign-btn">Assign to me</button>
                <textarea id="note-text" maxlength="400" rows="2" placeholder="Note (encrypted at rest)"></textarea>
                <button class="secondary" id="note-btn">Add note</button>
                <button class="primary" id="resolve-btn">Resolve</button>
            </div>` : ""}`;

        const bind = (elementId, handler) => { const el = document.getElementById(elementId); if (el) { el.addEventListener("click", handler); } };
        bind("assign-btn", async () => { await post(`/cases/${id}/assign`); openCase(id); });
        bind("note-btn", async () => { const note = document.getElementById("note-text").value.trim(); if (note) { await post(`/cases/${id}/notes`, { Note: note }); openCase(id); } });
        bind("resolve-btn", async () => { await post(`/cases/${id}/resolve`, { Note: document.getElementById("note-text").value.trim() }); openCase(id); });
        bind("reveal-btn", async () => {
            const r = await post(`/cases/${id}/reveal`, { Purpose: document.getElementById("reveal-purpose").value });
            const box = document.getElementById("contact-box");
            if (r.ok) { const p = await r.json(); box.innerHTML = `<strong>Contact:</strong> ${text(p.fullName)} · ${text(p.phone)} <span class="muted">(this reveal is in the access log)</span>`; }
            else { box.innerHTML = `<strong>Contact:</strong> <span class="muted">not available (${r.status})</span>`; }
        });
        if (!dialog.open) { dialog.showModal(); }
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/cases")
        .withAutomaticReconnect()
        .build();
    connection.on("caseUpserted", (c) => {
        grid.updateOrAddData([c]).then(() => {
            const row = grid.getRow(c.caseId);
            if (row) { row.getElement().classList.add("flash"); setTimeout(() => row.getElement().classList.remove("flash"), 1500); }
        });
        scheduleStats();
    });
    connection.on("responseRecorded", scheduleStats);
    const setLive = (on) => { live.textContent = on ? "live" : "offline"; live.classList.toggle("off", !on); };
    connection.onreconnecting(() => setLive(false));
    // After a reconnect the client may have missed pushes: reload once.
    connection.onreconnected(() => { setLive(true); loadCases(); loadStats(); });
    connection.onclose(() => setLive(false));

    loadCases();
    loadStats();
    connection.start().then(() => setLive(true)).catch(() => setLive(false));
    setInterval(() => grid.redraw(true), 60000); // keep "due in" labels fresh
})();
