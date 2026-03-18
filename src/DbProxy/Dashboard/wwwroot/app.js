const API_KEY = new URLSearchParams(window.location.search).get('key') || '';

async function fetchApi(path) {
    const res = await fetch(path, {
        headers: { 'X-Api-Key': API_KEY }
    });
    if (!res.ok) return null;
    return res.json();
}

function timeAgo(dateStr) {
    const d = dateStr.endsWith('Z') ? dateStr : dateStr + 'Z';
    const seconds = Math.floor((Date.now() - new Date(d).getTime()) / 1000);
    if (seconds < 60) return `${seconds}s ago`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
    return `${Math.floor(seconds / 86400)}d ago`;
}

function sessionStatus(s) {
    if (s.isRevoked) return '<span class="badge badge-red">Revoked</span>';
    if (new Date(s.expiresAt.endsWith('Z') ? s.expiresAt : s.expiresAt + 'Z') < Date.now()) return '<span class="badge badge-gray">Expired</span>';
    if (s.isConnected) return '<span class="badge badge-green">Connected</span>';
    return '<span class="badge badge-yellow">Idle</span>';
}

function budgetDisplay(s) {
    if (s.queryBudget == null) return `${s.queriesUsed}`;
    return `${s.queriesUsed} / ${s.queryBudget}`;
}

async function refresh() {
    const [sessions, queries] = await Promise.all([
        fetchApi('/api/sessions'),
        fetchApi('/api/queries?count=50')
    ]);

    if (sessions) {
        const active = sessions.filter(s => !s.isRevoked && new Date(s.expiresAt + 'Z') > Date.now());
        const connected = sessions.filter(s => s.isConnected);
        const totalQueries = sessions.reduce((sum, s) => sum + s.queriesUsed, 0);

        document.getElementById('stats').innerHTML = `
            <div class="stat-card"><div class="label">Active Sessions</div><div class="value">${active.length}</div></div>
            <div class="stat-card"><div class="label">Connected Now</div><div class="value">${connected.length}</div></div>
            <div class="stat-card"><div class="label">Total Queries</div><div class="value">${totalQueries}</div></div>
            <div class="stat-card"><div class="label">Total Sessions</div><div class="value">${sessions.length}</div></div>
        `;

        const tbody = document.getElementById('sessions');
        if (active.length === 0) {
            tbody.innerHTML = '<tr><td colspan="6" class="empty">No active sessions</td></tr>';
        } else {
            tbody.innerHTML = active.map(s => `
                <tr>
                    <td>${esc(s.agentId)}</td>
                    <td>${esc(s.task)}</td>
                    <td>${sessionStatus(s)}</td>
                    <td>${budgetDisplay(s)}</td>
                    <td>${timeAgo(s.createdAt)}</td>
                    <td>${timeAgo(s.lastActivityAt)}</td>
                </tr>
            `).join('');
        }
    }

    if (queries) {
        const tbody = document.getElementById('queries');
        if (queries.length === 0) {
            tbody.innerHTML = '<tr><td colspan="6" class="empty">No queries yet</td></tr>';
        } else {
            tbody.innerHTML = queries.map(q => `
                <tr>
                    <td>${timeAgo(q.timestamp)}</td>
                    <td>${esc(q.agentId)}</td>
                    <td class="query-text" title="${esc(q.query)}">${esc(q.query)}</td>
                    <td>${q.rowCount ?? '-'}</td>
                    <td>${q.durationMs}ms</td>
                    <td>${q.success ? '<span class="badge badge-green">OK</span>' : '<span class="badge badge-red">Error</span>'}</td>
                </tr>
            `).join('');
        }
    }
}

function esc(str) {
    const div = document.createElement('div');
    div.textContent = str || '';
    return div.innerHTML;
}

refresh();
setInterval(refresh, 3000);
