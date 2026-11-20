const captureAnchors = new Map(data.captures.map((capture, index) => [capture, 'capture-' + (index + 1)]));
const signed = (value, unit = '') => Number.isFinite(value)
    ? (value > 0 ? '+' : '') + number(value) + unit : 'Unavailable';
function badge(parent, status) {
    return text(parent, 'span', status, 'badge ' + status);
}
function statusKind(value) {
    if (/failed|error|canceled/.test(value ?? '')) return 'failed';
    if (/incomplete|unsupported|pending|running|skipped/.test(value ?? '')) return 'incomplete';
    if (/partial/.test(value ?? '')) return 'partial';
    if (/warning/.test(value ?? '')) return 'warning';
    if (['succeeded', 'complete', 'passed', 'compatible'].includes(value)) return 'complete';
    return 'incomplete';
}
function captureStatus(capture) {
    if (statusKind(capture.captureStatus) === 'failed' || capture.analysisError && capture.analysisStatus === 'failed') return 'failed';
    const status = statusKind(capture.analysisStatus ?? capture.captureStatus);
    return status === 'complete' && capture.analysisWarnings?.length ? 'warning' : status;
}
function link(parent, label, href, download = false) {
    if (!href) return;
    const anchor = text(parent, 'a', label);
    anchor.href = href;
    if (download) anchor.download = '';
}
function captureLinks(parent, capture) {
    const links = text(parent, 'div', '', 'links');
    link(links, 'Open perf report', capture.perfReportLink);
    link(links, 'Download raw perf.data', capture.rawPerfLink, true);
    link(links, 'Open normalized JSON', capture.normalizedLink);
    link(links, 'Open analysis.json', data.analysisLink);
    if (capture.stacks?.stacks?.length && parent.tagName !== 'DETAILS') {
        link(links, 'Open flamegraph', '#' + captureAnchors.get(capture));
    }
}
function makeSummary() {
    if (data.historyMode) return;
    const block = section('summary', 'Summary');
    const warnings = new Set(data.warnings ?? []);
    const statuses = data.captures.map(captureStatus);
    for (const capture of data.captures) {
        for (const message of [capture.analysisError, ...(capture.analysisWarnings ?? [])].filter(Boolean)) {
            warnings.add((capture.captureId ?? 'Capture') + ' · ' + message.trim().split('\n')[0]);
        }
    }

    const workflowStatus = data.status ? statusKind(data.status) : null;
    const status = workflowStatus === 'failed' ? 'failed'
        : statuses.length && statuses.every(value => value === 'failed') ? 'failed'
        : statuses.includes('incomplete') || workflowStatus === 'incomplete' ? 'incomplete'
        : statuses.some(value => value === 'partial' || value === 'failed') ? 'partial'
        : warnings.size || statuses.includes('warning') ? 'warning'
        : workflowStatus ?? (statuses.length ? 'complete' : 'incomplete');
    const head = text(block, 'p', 'Status ');
    badge(head, status);
    if (data.analysisLink) link(text(block, 'div', '', 'links'), 'Open analysis.json', data.analysisLink);
    const evidence = data.captures.filter(capture => Number.isFinite((capture.stacks ?? capture.summary)?.samples));
    if (evidence.length) {
        const grid = text(block, 'div', '', 'summary-grid');
        const stats = evidence.map(capture => capture.stacks ?? capture.summary);
        const sum = read => stats.every(row => Number.isFinite(read(row))) ? stats.reduce((total, row) => total + read(row), 0) : null;
        const missing = row => row.emptyStackSamples ?? (Number.isFinite(row.parsedSamples) ? Math.max(0, row.samples - row.parsedSamples) : null);
        for (const [label, value] of [['Samples total', sum(row => row.samples)], ['Decoded', sum(row => row.parsedSamples)],
            ['Missing stacks', sum(missing)]]) {
            const item = text(grid, 'div');
            text(item, 'strong', number(value));
            text(item, 'span', label);
        }
        text(block, 'p', 'Totals cover captures with recorded stack counts. Hot functions are ranked within each capture.', 'note');
    }
    if (warnings.size) {
        text(block, 'h3', 'Major warnings');
        const list = text(block, 'ul');
        [...warnings].slice(0, 12).forEach(message => text(list, 'li', message));
        if (warnings.size > 12) text(block, 'p', (warnings.size - 12) + ' additional warnings', 'note');
    }
    for (const capture of data.captures) {
        const title = text(block, 'h3', (capture.captureId ?? 'Capture') + ' ');
        badge(title, captureStatus(capture));
        captureLinks(block, capture);
        const stats = capture.stacks ?? capture.summary;
        const hot = stats?.hotFunctions?.slice(0, 10);
        if (hot?.length) {
            table(block, ['Hot function', 'Self samples', 'Share of decoded'], hot.map(frame => [
                frame.frame, number(frame.selfSamples), stats.parsedSamples > 0 ? number(100 * frame.selfSamples / stats.parsedSamples) + '%' : 'Unavailable'
            ]));
        }
    }
}

function profileDelta() {
    const timing = data.timing?.variants ?? [];
    const counters = data.counterComparison?.variants ?? [];
    const pmcCaptures = data.captures.filter(capture => capture.pmc && capture.captureId?.startsWith('counters_'));
    const baseline = data.counterComparison?.baseline ?? data.timing?.baseline ??
        (data.captures.some(capture => capture.captureId === 'capture_before') ? 'before'
            : data.captures.some(capture => capture.captureId === 'capture_baseline') ? 'baseline'
            : pmcCaptures.find(capture => /^counters_(before|baseline)$/.test(capture.captureId))?.captureId.slice(9));
    if (!baseline) return;
    const variants = new Set([...timing.map(row => row.name), ...counters.map(row => row.name),
        ...pmcCaptures.map(row => row.captureId.slice(9)),
        ...data.captures.filter(row => row.captureId?.startsWith('capture_')).map(row => row.captureId.slice(8))]);
    variants.delete(baseline);
    if (!variants.size) return;

    const block = section('delta', 'Profile and wall time deltas');
    const timingFor = name => timing.find(row => row.name === name);
    const counterFor = name => counters.find(row => row.name === name);
    const captureFor = name => data.captures.find(row => row.captureId === 'capture_' + name);
    const duration = (start, end) => {
        const elapsed = (Date.parse(end) - Date.parse(start)) / 1000;
        return elapsed >= 0 ? elapsed : null;
    };
    const left = captureFor(baseline);
    for (const name of variants) {
        const panel = text(block, 'details', '', 'capture');
        panel.open = variants.size === 1;
        text(panel, 'summary', baseline + ' → ' + name);
        const right = captureFor(name);
        const links = text(panel, 'div', '', 'links');
        for (const [label, capture] of [[baseline, left], [name, right]]) {
            if (capture?.stacks?.stacks?.length) link(links, label + ' flamegraph', '#' + captureAnchors.get(capture));
        }
        const timingRows = [];
        function wall(label, before, after) {
            if (!Number.isFinite(before) || !Number.isFinite(after)) return;
            timingRows.push([label, number(before), number(after), signed(after - before), before > 0 ? signed(100 * (after - before) / before, '%') : 'Unavailable']);
        }
        if (data.timing?.baseline === baseline && (!left || !right || left.backend === right.backend)) {
            wall('Uninstrumented process median', timingFor(baseline)?.medianSeconds, timingFor(name)?.medianSeconds);
        }
        if (left && right && left.backend === right.backend && left.mode === right.mode) {
            wall('Target elapsed time', duration(left.targetStartedAt, left.targetFinishedAt), duration(right.targetStartedAt, right.targetFinishedAt));
            wall('Capture elapsed time (includes profiler)', duration(left.startedAt, left.finishedAt), duration(right.startedAt, right.finishedAt));
        }
        if (timingRows.length) table(panel, ['Wall time', baseline + ' (s)', name + ' (s)', 'Δ (s)', 'Δ %'], timingRows);

        const a = counterFor(baseline);
        const b = counterFor(name);
        if (a && b) {
            const rows = [];
            for (const field of ['events', 'derived']) {
                for (const event of new Set([...Object.keys(a[field] ?? {}), ...Object.keys(b[field] ?? {})])) {
                    const from = a[field]?.[event];
                    const to = b[field]?.[event];
                    const ratio = to?.ratioToBaseline;
                    const comparable = from && to && Number.isFinite(ratio) && from.unit === to.unit && from.domain === to.domain;
                    rows.push([event, to?.unit || from?.unit || 'events', number(from?.value), number(to?.value),
                        comparable ? signed(to.value - from.value) : 'Unavailable', comparable ? signed(100 * (ratio - 1), '%') : 'Unavailable']);
                }
            }
            if (rows.length) {
                text(panel, 'h3', 'Counter deltas');
                text(panel, 'p', data.counterComparison.comparability, 'note');
                table(panel, ['Event', 'Unit', baseline, name, 'Δ', 'Δ %'], rows);
            }
        }
        pmcDelta(panel, pmcCaptures.find(capture => capture.captureId === 'counters_' + baseline)?.pmc,
            pmcCaptures.find(capture => capture.captureId === 'counters_' + name)?.pmc, baseline, name);
        const as = left?.stacks ?? left?.summary;
        const bs = right?.stacks ?? right?.summary;
        if (as?.parsedSamples > 0 && bs?.parsedSamples > 0 && left.backend === right.backend && left.mode === right.mode) {
            const shares = stats => new Map((stats.hotFunctions ?? []).map(row => [row.frame, 100 * row.selfSamples / stats.parsedSamples]));
            const first = shares(as);
            const second = shares(bs);
            const rows = [...new Set([...first.keys(), ...second.keys()])]
                .sort((x, y) => Math.max(first.get(y) ?? 0, second.get(y) ?? 0) - Math.max(first.get(x) ?? 0, second.get(x) ?? 0))
                .slice(0, 10).map(frame => [frame, number(first.get(frame)), number(second.get(frame)),
                    first.has(frame) && second.has(frame) ? signed(second.get(frame) - first.get(frame)) : 'Unavailable']);
            if (rows.length) {
                text(panel, 'h3', 'Hottest frames by self sample share');
                text(panel, 'p', 'Δ uses frames ranked in both captures. Shares use decoded samples as the denominator.', 'note');
                table(panel, ['Frame', baseline + ' (%)', name + ' (%)', 'Δ (pp)'], rows);
            }
        }
        if (!timingRows.length) text(panel, 'p', 'Wall time unavailable for this pair.', 'note');
    }
}

function pmcDelta(parent, before, after, baseline, candidate) {
    if (!before?.counters?.length || !after?.counters?.length) return;
    const matchedConfiguration = before.configurationSha256 && before.configurationSha256 === after.configurationSha256
        && before.orderedEvents === true && after.orderedEvents === true && before.attribution === after.attribution;
    const from = new Map(before.counters.map(row => [row.name, row]));
    const to = new Map(after.counters.map(row => [row.name, row]));
    const rows = [...new Set([...from.keys(), ...to.keys()])].map(name => {
        const a = from.get(name), b = to.get(name);
        const comparable = matchedConfiguration && a?.status === 'complete' && b?.status === 'complete'
            && a.interval === b.interval && a.role === b.role
            && Number.isFinite(a.estimatedEvents) && Number.isFinite(b.estimatedEvents);
        return [name, number(a?.estimatedEvents), number(b?.estimatedEvents),
            comparable ? signed(b.estimatedEvents - a.estimatedEvents) : 'Unavailable',
            comparable && a.estimatedEvents > 0 ? signed(100 * (b.estimatedEvents / a.estimatedEvents - 1), '%') : 'Unavailable'];
    });
    text(parent, 'h3', 'ETW counter deltas');
    text(parent, 'p', 'Overflow-based estimates. Deltas require matching configurations, intervals and verified target samples.', 'note');
    table(parent, ['Source', baseline + ' (estimated)', candidate + ' (estimated)', 'Δ', 'Δ %'], rows);
}

makeSummary();
profileDelta();
