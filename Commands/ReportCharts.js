function groupBy(rows, key) {
    const groups = new Map();
    for (const row of rows) {
        const id = key(row);
        if (!groups.has(id)) groups.set(id, []);
        groups.get(id).push(row);
    }
    return groups;
}
const valuesOf = rows => rows.map(row => row.value);
const shorten = (value, length = 26) => value.length > length ? value.slice(0, length - 1) + '…' : value;
const workloadKey = (meta, exclude = []) => JSON.stringify(Object.entries(meta).filter(([key]) => !exclude.includes(key)).sort());
const metricKey = row => JSON.stringify([row.metric, row.unit, row.method, row.direction ?? null]);
const seriesKey = row => JSON.stringify([row.metric, row.unit, row.method, row.variant, row.scenario]);
const family = row => [row.target, row.configuration, row.scenario].filter(Boolean).join(' · ') || 'Default workload';
const quantile = (sorted, probability) => {
    const index = probability * (sorted.length - 1);
    const fraction = index % 1;
    return fraction ? (1 - fraction) * sorted[Math.floor(index)] + fraction * sorted[Math.ceil(index)] : sorted[index];
};
function box(plot, values, x, y) {
    const sorted = [...values].sort((a, b) => a - b);
    const q1 = quantile(sorted, .25), q3 = quantile(sorted, .75);
    const iqr = q3 - q1;
    const lo = sorted.find(value => value >= q1 - 1.5 * iqr);
    const hi = sorted.findLast(value => value <= q3 + 1.5 * iqr);
    svg(plot, 'line', {x1: x(lo), x2: x(hi), y1: y, y2: y});
    for (const value of [lo, hi]) svg(plot, 'line', {x1: x(value), x2: x(value), y1: y - 6, y2: y + 6});
    svg(plot, 'rect', {x: x(q1), y: y - 8, width: Math.max(1, x(q3) - x(q1)), height: 16, class: 'box'});
}
function chart(parent, label, height) {
    return svg(parent, 'svg', {viewBox: '0 0 900 ' + height, class: 'plot', role: 'img', 'aria-label': label});
}
function domain(values, a, b) {
    let min = Infinity, max = -Infinity;
    for (const value of values) {
        if (!Number.isFinite(value)) continue;
        min = Math.min(min, value);
        max = Math.max(max, value);
    }
    if (!Number.isFinite(min)) min = max = 0;
    const scale = Math.max(Math.abs(min), Math.abs(max)) || 1;
    const span = max / scale - min / scale;
    return {at: value => span ? a + (b - a) * (value / scale - min / scale) / span : (a + b) / 2, min, max};
}
function xy(parent, label, points, xLabel, yLabel, options = {}) {
    if (!points.length) return;
    const height = 330, bottom = 280;
    const plot = chart(parent, label, height);
    const x = domain(points.map(point => point.x), 92, 870);
    const y = domain([...points.map(point => point.y), ...(options.limits ?? [])], bottom, 30);
    const xFormat = options.xFormat ?? number;
    for (const fraction of [0, .25, .5, .75, 1]) {
        const xv = x.min * (1 - fraction) + x.max * fraction;
        const yv = y.min * (1 - fraction) + y.max * fraction;
        if (x.min !== x.max || fraction === 0) svg(plot, 'text', {x: x.at(xv), y: bottom + 20, 'text-anchor': 'middle'}, xFormat(xv));
        if (y.min === y.max && fraction !== 0) continue;
        svg(plot, 'line', {x1: 92, x2: 870, y1: y.at(yv), y2: y.at(yv)});
        svg(plot, 'text', {x: 84, y: y.at(yv) + 4, 'text-anchor': 'end'}, number(yv));
    }
    svg(plot, 'text', {x: 92, y: 16}, yLabel);
    svg(plot, 'text', {x: 481, y: height - 8, 'text-anchor': 'middle'}, xLabel);
    for (const value of options.limits ?? []) svg(plot, 'line', {x1: 92, x2: 870, y1: y.at(value), y2: y.at(value), class: 'bound'});
    const ordered = [...points].sort((a, b) => a.x - b.x);
    svg(plot, 'polyline', {points: ordered.map(point => x.at(point.x) + ',' + y.at(point.y)).join(' '), class: 'series'});
    for (const point of ordered) {
        const dot = svg(plot, 'circle', {cx: x.at(point.x), cy: y.at(point.y), r: 5, class: 'point'});
        svg(dot, 'title', {}, point.label ?? xFormat(point.x) + ' · ' + number(point.y));
    }
}
function matrixCells(rows, [xAxis, yAxis]) {
    return [...groupBy(rows, row => JSON.stringify([row.meta[xAxis], row.meta[yAxis]])).values()].map(cell => ({
        x: cell[0].meta[xAxis], y: cell[0].meta[yAxis], value: median(valuesOf(cell)), count: cell.length
    }));
}
function heatmap(parent, cells, [xAxis, yAxis], label, direction) {
    const columns = [...new Set(cells.map(cell => cell.x))].sort((a, b) => a - b);
    const lines = [...new Set(cells.map(cell => cell.y))].sort((a, b) => a - b);
    const lookup = new Map(cells.map(cell => [JSON.stringify([cell.x, cell.y]), cell]));
    const width = 770 / columns.length;
    const shade = domain(cells.map(cell => cell.value), .16, .88);
    const maxDelta = cells.reduce((max, cell) => Number.isFinite(cell.value) ? Math.max(max, Math.abs(cell.value)) : max, 0);
    const percent = value => signed(value, '%');
    const improvement = value => direction === 'lower-is-better' ? -value : value;
    if (direction) text(parent, 'p', 'Green · improvement. Red · regression. Δ% = 100 × (candidate median − baseline median) / |baseline median|.', 'note');
    const plot = chart(parent, label, 60 + lines.length * 38);
    svg(plot, 'text', {x: 0, y: 17}, yAxis.replaceAll('_', ' ') + ' ↓');
    svg(plot, 'text', {x: 130, y: 17}, xAxis.replaceAll('_', ' ') + ' →');
    columns.forEach((value, i) => svg(plot, 'text', {x: 110 + (i + .5) * width, y: 40, 'text-anchor': 'middle'}, number(value)));
    lines.forEach((value, j) => {
        const y = 48 + j * 38;
        svg(plot, 'text', {x: 95, y: y + 21, 'text-anchor': 'end'}, number(value));
        columns.forEach((column, i) => {
            const cell = lookup.get(JSON.stringify([column, value]));
            const x = 110 + i * width;
            const hasValue = Number.isFinite(cell?.value);
            const opacity = hasValue ? (direction ? .16 + .72 * Math.abs(cell.value) / (maxDelta || 1) : shade.at(cell.value)) : 0;
            const result = direction && hasValue ? (improvement(cell.value) > 0 ? 'better' : improvement(cell.value) < 0 ? 'worse' : 'neutral') : '';
            const mark = svg(plot, 'rect', {x, y, width: Math.max(0, width - 2), height: 36,
                class: hasValue ? 'box ' + result : '', style: hasValue ? 'fill-opacity:' + opacity : 'fill:none;stroke:var(--line)'});
            const detail = direction ? (hasValue ? percent(cell.value) + ' · ' + result : cell?.reason)
                : hasValue ? 'Median ' + number(cell.value) + ' · n=' + cell.count : null;
            svg(mark, 'title', {}, `${xAxis}=${column} · ${yAxis}=${value} · ${detail ?? 'No measurements'}`);
            if (width >= 34) svg(plot, 'text', {x: x + width / 2, y: y + 23, 'text-anchor': 'middle',
                style: !direction && opacity > .5 ? 'fill:var(--paper)' : ''},
                hasValue ? (direction ? percent(cell.value) : number(cell.value)) : '—');
        });
    });
    if (direction) {
        table(parent, [xAxis, yAxis, 'Baseline median (n)', 'Candidate median (n)', 'Δ %', 'Effect'], cells.map(cell => [
            number(cell.x), number(cell.y), cell.before ? number(cell.before.value) + ' (' + cell.before.count + ')' : 'Unavailable',
            cell.after ? number(cell.after.value) + ' (' + cell.after.count + ')' : 'Unavailable', percent(cell.value),
            cell.reason ?? (improvement(cell.value) > 0 ? 'Improvement' : improvement(cell.value) < 0 ? 'Regression' : 'No change')
        ]));
    } else {
        table(parent, [xAxis, yAxis, 'Median', 'Measurements'], cells.map(cell =>
            [number(cell.x), number(cell.y), number(cell.value), number(cell.count)]));
    }
}
const coordinateValue = value => typeof value === 'string' && /^[-+]?\d+(\.\d+)?([eE][-+]?\d+)?$/.test(value) ? Number(value) : value;
const argumentKey = key => key.replace(/^--/, '').replaceAll('-', '_').toLowerCase();
function parseWorkload(input) {
    if (typeof input === 'string') {
        try { input = JSON.parse(input); } catch { return {recorded_workload: input}; }
    }
    const args = Array.isArray(input) ? input : input?.arguments ?? input?.args;
    const meta = Object.create(null);
    const set = (key, value) => {
        meta[argumentKey(key)] = coordinateValue(value);
    };
    if (input && typeof input === 'object' && !Array.isArray(input)) {
        for (const [key, value] of Object.entries(input)) {
            if (key !== 'args' && key !== 'arguments') set(key, value);
        }
    }
    if (Array.isArray(args)) {
        for (let i = 0; i < args.length; i++) {
            const arg = String(args[i]);
            if (!arg.startsWith('--')) {
                set('argument_' + i, arg);
                continue;
            }
            const equals = arg.indexOf('=');
            if (equals > 2) set(arg.slice(0, equals), arg.slice(equals + 1));
            else if (i + 1 < args.length && !String(args[i + 1]).startsWith('--')) set(arg, args[++i]);
            else set(arg, true);
        }
    }
    return meta;
}
function workload(input, trial) {
    const meta = parseWorkload(input);
    for (const axis of trial?.axes ?? []) {
        delete meta[argumentKey(axis.flag)];
    }
    for (const axis of trial?.axes ?? []) {
        meta[axis.name] = coordinateValue(axis.value);
    }
    return meta;
}
function workflowMeasurements() {
    const rows = [];
    for (const node of data.nodes) {
        if (node.kind !== 'run' || node.status !== 'succeeded') continue;
        const meta = workload(node.arguments, node.benchmarkTrial);
        const timing = /^timing_(.+?)(?:_(\d{4}))?_(\d+)$/.exec(node.id);
        const details = {nodeId: node.id, target: node.target, configuration: node.configuration,
            variant: timing?.[1] ?? null, scenario: meta.scenario ?? null,
            sampleIndex: timing ? Number(timing[3]) - 1 : null, meta, benchmarkTrial: node.benchmarkTrial};
        for (const sample of node.metrics ?? []) rows.push({...details, metric: sample.name, unit: sample.unit, value: sample.value,
            method: 'Benchmark stdout JSON · ' + sample.path, direction: sample.direction});
        if (timing && Number.isFinite(node.processDurationSeconds)) rows.push({...details, metric: 'process_wall_time', unit: 's',
            method: 'Uninstrumented process wall time', value: node.processDurationSeconds, direction: 'lower-is-better'});
    }
    return rows;
}
const runTable = new Map((data.runs ?? []).map(row => [row.id, row]));
const measurementRows = [...(data.measurements ?? []).map(row => {
    const meta = workload(row.workload, row.benchmarkTrial);
    const timing = /^timing_(.+?)_(\d{4})_\d+$/.exec(row.nodeId ?? '');
    const variant = timing && row.variant === timing[1] + '_' + timing[2] ? timing[1] : row.variant;
    const scenario = timing && row.scenario === 'case_' + timing[2] ? null : row.scenario;
    return {...row, meta, variant, direction: row.direction ?? (row.metric === 'process_wall_time' ? 'lower-is-better' : null),
        indexedVariant: row.variant, caseId: row.scenario,
        scenario: row.method === 'dagflow-harness' ? row.scenario : meta.scenario ?? scenario};
}), ...workflowMeasurements()].filter(row => Number.isFinite(row.value));

function pointDetails(row) {
    return [row.nodeId, Number.isInteger(row.sampleIndex) ? 'Sample ' + (row.sampleIndex + 1) : null, number(row.value)].filter(Boolean).join(' · ');
}
function dumbbell(parent, rows, label, unit) {
    const displayed = rows.slice(0, 40);
    const x = domain(displayed.flatMap(row => [...row.left, ...row.right, row.a, row.b]), 224, 875);
    const height = 34 + displayed.length * 36;
    const plot = chart(parent, label, height);
    const legend = text(parent, 'p', '', 'note');
    text(legend, 'span', 'Baseline', 'color-baseline');
    legend.append(' · ');
    text(legend, 'span', 'Candidate', 'color-candidate');
    legend.append(' · Small points are individual measurements.');
    displayed.forEach((row, i) => {
        const y = 17 + i * 36;
        const title = svg(plot, 'text', {x: 0, y: y + 4}, shorten(row.label, 31));
        svg(title, 'title', {}, row.label);
        if (row.connect && Number.isFinite(row.a) && Number.isFinite(row.b)) {
            svg(plot, 'line', {x1: x.at(row.a), x2: x.at(row.b), y1: y, y2: y, class: 'interval'});
        }
        for (const [raw, value, style, offset] of [[row.left, row.a, 'baseline', -7], [row.right, row.b, 'candidate', 7]]) {
            const step = Math.max(1, Math.ceil(raw.length / 400));
            raw.forEach((point, index) => {
                if (index % step) return;
                const mark = svg(plot, 'circle', {cx: x.at(point), cy: y + offset + (index % 3 - 1) * 2, r: 3, class: 'raw ' + style});
                svg(mark, 'title', {}, number(point) + ' ' + unit);
            });
            if (Number.isFinite(value)) {
                const mark = svg(plot, 'circle', {cx: x.at(value), cy: y, r: 5, class: 'point ' + style});
                svg(mark, 'title', {}, 'Median · ' + number(value) + ' ' + unit);
            }
        }
    });
    svg(plot, 'text', {x: 224, y: height - 3}, number(x.min));
    svg(plot, 'text', {x: 875, y: height - 3, 'text-anchor': 'end'}, number(x.max) + ' ' + unit);
    if (displayed.length < rows.length || displayed.some(row => row.left.length > 400 || row.right.length > 400)) {
        text(parent, 'p', 'Plot shows up to 40 rows and 400 individual points per side. All rows are listed below.', 'note');
    }
}
const comparisonSamples = data.baseline ? groupBy(measurementRows, sample =>
    JSON.stringify([sample.runId, seriesKey({...sample, variant: sample.indexedVariant, scenario: sample.caseId})])) : new Map();
function rawComparison(row) {
    return [[data.baseline, data.baselineVariant], [data.candidate, data.candidateVariant]].map(([run, variant]) => {
        const samples = comparisonSamples.get(JSON.stringify([run, seriesKey({...row, variant: variant ?? row.variant})])) ?? [];
        return valuesOf(data.paired ? [...samples].sort((a, b) => a.sampleIndex - b.sampleIndex) : samples);
    });
}
function withinRunComparison(parent, rows, unit) {
    const grouped = groupBy(rows.filter(row => row.variant), row => JSON.stringify([row.runId, row.nodeId && row.method === 'dagflow-harness' ? row.nodeId : null,
        family(row), workloadKey(row.meta), row.environment]));
    const pairs = [];
    for (const group of grouped.values()) {
        const variants = groupBy(group, row => row.variant);
        if (variants.size < 2) continue;
        const first = [group[0].benchmarkTrial?.baselineVariant, data.timing?.baseline, 'baseline', 'before']
            .find(name => variants.has(name)) ?? variants.keys().next().value;
        const left = variants.get(first);
        for (const [name, right] of variants) {
            if (name === first) continue;
            const counts = groupBy(left, row => row.sampleIndex);
            const matches = [...groupBy(right, row => row.sampleIndex)]
                .filter(([index, samples]) => Number.isInteger(index) && counts.get(index)?.length === 1 && samples.length === 1)
                .map(([index, samples]) => ({index, before: counts.get(index)[0].value, after: samples[0].value}));
            pairs.push({label: family(group[0]) + ' · ' + first + ' → ' + name, workload: group[0].meta,
                left: valuesOf(left), right: valuesOf(right), a: median(valuesOf(left)), b: median(valuesOf(right)), connect: true,
                matches, runId: group[0].runId});
        }
    }
    if (!pairs.length) return;
    text(parent, 'h3', 'Variant comparison');
    dumbbell(parent, pairs, 'Baseline and candidate medians with individual measurements', unit);
    table(parent, ['Case', 'Workload', 'Baseline median', 'Candidate median', 'Δ medians', 'Matched samples', 'Median paired Δ'],
        pairs.map(row => [row.label, [row.runId, workloadLabel(row.workload)].filter(Boolean).join(' · '),
            number(row.a), number(row.b), signed(row.b - row.a), number(row.matches.length),
            row.matches.length ? signed(median(row.matches.map(match => match.after - match.before))) : 'Unavailable']));
    if (pairs.some(row => row.matches.length)) {
        expandableChart(parent, 'Matched trial measurements', false, item => {
            table(item, ['Case', 'Workload', 'Trial', 'Baseline', 'Candidate', 'Δ'], pairs.flatMap(row => row.matches.map(match => [
                row.label, workloadLabel(row.workload), number(match.index + 1), number(match.before),
                number(match.after), signed(match.after - match.before)
            ])));
        });
    }
}
function workloadLabel(meta) {
    return Object.entries(meta).map(([key, value]) => key + '=' + (typeof value === 'object' ? JSON.stringify(value) : value)).join(' · ');
}
function xyGroups(parent, rows, axis, label, efficiency) {
    const points = [...groupBy(rows, row => row.meta[axis])].map(([x, group]) => ({
        x, y: median(valuesOf(group)), label: axis + '=' + x + ' · ' + number(median(valuesOf(group))) + ' · n=' + group.length, count: group.length
    })).sort((a, b) => a.x - b.x);
    const row = rows[0];
    const unit = row.unit || row.metric;
    xy(parent, label, points, axis.replaceAll('_', ' '), unit);
    const first = points[0];
    const singleWorker = first.x === 1 && first.y > 0;
    if (efficiency && singleWorker) {
        const ratio = point => 100 * (point.y / first.y) / point.x;
        text(parent, 'h4', 'Parallel efficiency relative to one worker');
        xy(parent, 'Parallel efficiency', points.map(point => ({x: point.x, y: ratio(point), label: number(ratio(point)) + '%'})),
            'workers', 'Efficiency (%)');
    }
    table(parent, [axis, 'Median', 'Measurements'], points.map(point => [number(point.x), number(point.y), number(point.count)]));
}
function expandableChart(parent, title, open, draw) {
    const item = text(parent, 'details', '', 'capture');
    text(item, 'summary', title);
    if (open) {
        item.open = true;
        draw(item);
    } else {
        item.addEventListener('toggle', () => {
            if (!item.open || item.dataset.rendered) return;
            draw(item);
            item.dataset.rendered = 'true';
        });
    }
}
function relativeCells(baseline, candidate, axes) {
    const left = new Map(matrixCells(baseline, axes).map(cell => [JSON.stringify([cell.x, cell.y]), cell]));
    const right = new Map(matrixCells(candidate, axes).map(cell => [JSON.stringify([cell.x, cell.y]), cell]));
    return [...new Set([...left.keys(), ...right.keys()])].map(key => {
        const before = left.get(key), after = right.get(key);
        const [x, y] = JSON.parse(key);
        if (!before || !after) return {x, y, before, after, reason: 'Missing ' + (before ? 'candidate' : 'baseline')};
        if (before.value === 0) return {x, y, before, after, reason: 'Zero baseline'};
        const value = (after.value - before.value) / Math.abs(before.value) * 100;
        return Number.isFinite(value) ? {x, y, before, after, value} : {x, y, before, after, reason: 'Δ outside numeric range'};
    });
}
function drawWorkloads(parent, rows, mode) {
    if (!rows.length) return;
    const shapes = groupBy(rows, row => JSON.stringify(row.benchmarkTrial?.axes?.map(axis => [axis.name, axis.role]) ?? null));
    if (shapes.size > 1) {
        for (const group of shapes.values()) drawWorkloads(parent, group, mode);
        return;
    }
    const first = rows[0];
    const declared = first.benchmarkTrial?.axes;
    const legacy = ['workers', 'shards', 'arrival_rate'];
    const axisFor = (role, name) => declared
        ? declared.find(axis => axis.role === role)?.name
            ?? declared.find(axis => !axis.role && (axis.name === name || argumentKey(axis.flag) === name))?.name
        : name;
    const workers = axisFor('workers', 'workers');
    const shards = axisFor('shards', 'shards');
    const arrival = axisFor('arrival-rate', 'arrival_rate');
    const matrixX = axisFor('matrix-x', undefined);
    const matrixY = axisFor('matrix-y', undefined);
    const dims = (declared
        ? declared.filter(axis => axis.role !== 'facet').map(axis => axis.name)
        : [...new Set(rows.flatMap(row => Object.keys(row.meta)))].filter(key => !['scenario', 'warmup', 'warmup_ms', 'repeats', 'sample_stride'].includes(key)))
        .filter(key => new Set(rows.map(row => row.meta[key]).filter(Number.isFinite)).size > 1);
    const throughput = /throughput|tasks_per_(sec|second)|tasks\/s|ops\/s/i.test(first.metric + ' ' + first.unit);
    const latency = /latency|(?:start|finish|run)_p\d|scheduled[-_ ]start|submit[-_ ]call|producer[-_ ]lag|backpressure/i
        .test(first.metric + ' ' + first.method);
    const sweeps = mode === 'matrix' ? [] : [
        ...(latency && dims.includes(arrival) ? [[arrival, 'Saturation curve']] : []),
        ...(throughput && dims.includes(workers) ? [[workers, 'Scalability curve']] : []),
        ...(mode === 'sweep' ? dims.map(key => [key, 'Sweep · ' + key]) : [])
    ].filter(([key], index, items) => items.findIndex(([other]) => other === key) === index);
    const groups = (axes, accept, splitVariants = true) => [...groupBy(rows.filter(accept), row => JSON.stringify([
        row.runId, splitVariants ? row.variant : row.benchmarkTrial?.baselineVariant, family(row), row.environment, workloadKey(row.meta, axes)
    ])).values()];
    let rendered = 0;
    function add(group, axes, title, draw, variant = group[0].variant) {
        const row = group[0];
        const fixed = Object.entries(row.meta).filter(([key]) => !axes.includes(key));
        const settings = workloadLabel(Object.fromEntries(fixed.filter(([key]) => dims.includes(key))));
        expandableChart(parent, [title, variant, family(row), settings].filter(Boolean).join(' · '),
            rendered++ === 0, item => {
                text(item, 'p', [row.runId, workloadLabel(Object.fromEntries(fixed))].filter(Boolean).join(' · '), 'note');
                draw(item);
            });
    }
    function drawMatrix(axes) {
        if (!axes.every(axis => dims.includes(axis))) return false;
        const numeric = row => axes.every(key => Number.isFinite(row.meta[key]));
        const spans = group => axes.every(key => new Set(group.map(row => row.meta[key])).size > 1);
        const slices = groups(axes, numeric).filter(spans);
        const title = axes.join(' × ').replaceAll('_', ' ');
        let relative = false;
        if (['lower-is-better', 'higher-is-better'].includes(first.direction)) {
            for (const group of groups(axes, numeric, false).filter(spans)) {
                const variants = groupBy(group.filter(row => row.variant), row => row.variant);
                const name = [group[0].benchmarkTrial?.baselineVariant, data.timing?.baseline, 'baseline', 'before']
                    .find(variant => variants.has(variant));
                if (!name) continue;
                for (const [candidate, samples] of variants) {
                    if (candidate === name) continue;
                    const before = variants.get(name);
                    if (!spans([...before, ...samples])) continue;
                    relative = true;
                    add(group, axes, 'Relative benchmark matrix · ' + title, item => heatmap(item,
                        relativeCells(before, samples, axes), axes, 'Relative ' + title + ' · ' + first.metric, first.direction),
                    name + ' → ' + candidate);
                }
            }
        }
        for (const group of slices) add(group, axes, 'Benchmark matrix · ' + title,
            item => heatmap(item, matrixCells(group, axes), axes, title + ' · ' + first.metric));
        return relative || slices.length > 0;
    }
    if (mode !== 'sweep' && !drawMatrix([matrixX, matrixY]) && !drawMatrix([workers, shards]) && mode === 'matrix') {
        const ordered = [...new Set([...(declared ? [workers, shards, arrival] : legacy), ...dims])].filter(axis => dims.includes(axis));
        const pairs = ordered.flatMap((axis, index) => ordered.slice(index + 1).map(other => [axis, other]));
        pairs.some(drawMatrix);
    }
    for (const [axis, title] of sweeps) {
        for (const group of groups([axis], row => Number.isFinite(row.meta[axis]) &&
            ((axis !== arrival && axis !== workers) || row.meta[axis] > 0))) {
            if (new Set(group.map(row => row.meta[axis])).size < 2) continue;
            add(group, [axis], title, item => xyGroups(item, group, axis, title, axis === workers && throughput));
        }
    }
    if (!rendered && ['matrix', 'sweep'].includes(mode)) text(parent, 'p', 'No varying recorded axes for this selection.', 'note');
    if (!['matrix', 'sweep'].includes(mode)) withinRunComparison(parent, rows, first.unit);
    const distribution = groupBy(rows, row => JSON.stringify([row.runId, family(row), row.variant, row.environment, workloadKey(row.meta)]));
    text(parent, 'h3', 'Distribution');
    drawMetric(parent, {name: first.metric, unit: first.unit, method: first.method,
        groups: new Map([...distribution.values()].map((group, i) => {
            const row = group[0];
            return [[number(i + 1), row.variant, family(row), workloadLabel(row.meta), row.runId].filter(Boolean).join(' · '),
                group.map(sample => ({label: pointDetails(sample), value: sample.value}))];
        }))});
}

function historyComparison(parent, rows, first) {
    text(parent, 'p', 'Baseline ' + data.baseline + ' · Candidate ' + data.candidate, 'note');
    const comparisons = rows.map(row => {
        const [left, right] = rawComparison(row);
        return {label: [row.variant, row.scenario].filter(Boolean).join(' · ') || row.metric,
            a: left.length === row.baseline?.count ? row.baseline?.median : null,
            b: right.length === row.candidate?.count ? row.candidate?.median : null,
            left, right, connect: row.status === 'compatible'};
    });
    dumbbell(parent, comparisons, 'Run comparison · medians and individual measurements', first.unit);
    table(parent, ['Case', 'Baseline median', 'Candidate median', 'Δ medians', 'Δ %', 'Status', 'Reason'],
        rows.map((row, i) => [comparisons[i].label, number(comparisons[i].a), number(comparisons[i].b),
            signed(row.absoluteChange), signed(row.percentChange, '%'), row.status, row.reason]));
}

function bootstrapInterval(left, right, alpha, paired) {
    if (left.length < 10 || right.length < 10) return null;
    let seed = 0x30cf6383;
    const random = size => {
        seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
        return Math.floor(size * (seed / 4294967296));
    };
    const resample = values => median(Array.from(values, () => values[random(values.length)]));
    const estimates = [];
    const count = Math.min(999, Math.floor(2_000_000 / (left.length + right.length)));
    if (count < 399) return null;
    for (let i = 0; i < count; i++) {
        let effect;
        if (paired) {
            const sample = Array.from(left, () => random(left.length));
            const base = median(sample.map(index => left[index]));
            const change = median(sample.map(index => right[index] - left[index]));
            effect = 100 * change / Math.abs(base);
        } else {
            const base = resample(left);
            effect = 100 * (resample(right) - base) / base;
        }
        if (Number.isFinite(effect)) estimates.push(effect);
    }
    if (estimates.length < count * .95) return null;
    estimates.sort((a, b) => a - b);
    return [quantile(estimates, alpha / 2), quantile(estimates, 1 - alpha / 2)];
}
function historyEffects(parent, rows) {
    const settings = data.detection;
    if (data.paired) {
        text(parent, 'p', 'Paired detection · median of candidate − baseline within matching sample indices.', 'note');
        if (data.baselineVariant) text(parent, 'p', data.baselineVariant + ' → ' + data.candidateVariant, 'note');
    }
    const sign = settings.direction === 'higher-worse' ? 1 : -1;
    const threshold = settings.minimumEffectPercent * sign;
    const effects = rows.map(row => {
        const interval = Number.isFinite(row.pValue) ? bootstrapInterval(...rawComparison(row), settings.alpha, data.paired) : null;
        return {row, interval, label: [row.variant, row.scenario].filter(Boolean).join(' · ') || row.metric};
    });
    const plotted = effects.filter(effect => Number.isFinite(effect.row.percentChange)).slice(0, 40);
    if (plotted.length) {
        const height = 45 + plotted.length * 27;
        const plot = chart(parent, 'Effect plot with bootstrap intervals', height);
        const x = domain([0, threshold, ...plotted.flatMap(effect => [effect.row.percentChange, ...(effect.interval ?? [])])], 225, 875);
        for (const value of [0, threshold]) {
            svg(plot, 'line', {x1: x.at(value), x2: x.at(value), y1: 5, y2: height - 23, class: value === 0 ? '' : 'bound'});
        }
        plotted.forEach(({row, label, interval}, index) => {
            const y = 16 + index * 27;
            const caption = svg(plot, 'text', {x: 0, y: y + 4}, shorten(label, 31));
            svg(caption, 'title', {}, label);
            if (interval) svg(plot, 'line', {x1: x.at(interval[0]), x2: x.at(interval[1]), y1: y, y2: y, class: 'interval'});
            const dot = svg(plot, 'circle', {cx: x.at(row.percentChange), cy: y, r: 5, class: row.status === 'regression' ? 'point candidate' : 'point'});
            svg(dot, 'title', {}, signed(row.percentChange, '%') + ' · ' + row.status);
        });
        for (const [value, align] of [[x.min, 'start'], [x.max, 'end']]) svg(plot, 'text',
            {x: x.at(value), y: height - 3, 'text-anchor': align}, number(value) + '%');
        const resampling = data.paired ? 'resampling complete pairs' : 'assuming independent observations';
        text(parent, 'p', `Dashed line · regression threshold ${signed(threshold, '%')}. Descriptive ${number(100 * (1 - settings.alpha))}% percentile bootstrap intervals use ${resampling}. Verdicts use the ${settings.test}.`, 'note');
    }
    table(parent, ['Case', 'Effect', 'Bootstrap interval', 'Holm p', 'Status', 'Reason'],
        effects.map(({row, label, interval}) => [label, signed(row.percentChange, '%'),
            interval ? interval.map(value => signed(value, '%')).join(' to ') : 'Unavailable',
            number(row.adjustedPValue), row.status, row.reason]));
}
function compilerInfo(run) {
    return (run?.buildEnvironments ?? []).map(build => [build.compilerId ?? build.compiler, build.compilerVersion,
        build.configuration, build.generator].filter(Boolean).join(' ')).filter(Boolean).join(' · ');
}
function runMetadata(run) {
    return [run?.id, run?.revision, compilerInfo(run), run?.hostId].filter(Boolean).join(' · ');
}
function timeSeries(parent, measurements, first) {
    const grouped = groupBy(measurements.filter(row => runTable.has(row.runId)), row => {
        const run = runTable.get(row.runId);
        return JSON.stringify([row.variant, row.scenario, workloadKey(row.meta), row.environment,
            run.workflow, run.hostId ?? run.id, run.environmentFingerprint ?? run.id, compilerInfo(run)]);
    });
    let opened = false;
    for (const rows of [...grouped.values()].sort((a, b) =>
        new Set(b.map(row => row.runId)).size - new Set(a.map(row => row.runId)).size)) {
        const series = [...groupBy(rows, row => row.runId)].map(([id, items]) => {
            const run = runTable.get(id);
            return {run, time: Date.parse(run.startedAt), y: median(valuesOf(items)), count: items.length};
        }).sort((a, b) => (a.time || 0) - (b.time || 0));
        const byDate = series.every(point => Number.isFinite(point.time)) && new Set(series.map(point => point.time)).size === series.length;
        const sample = rows[0];
        const title = [sample.variant, sample.scenario, workloadLabel(sample.meta), series[0].run.hostId].filter(Boolean).join(' · ') || 'Benchmark history';
        expandableChart(parent, title + ' · ' + series.length + ' runs', !opened, group => {
            // Individuals chart with a fixed reference window and moving ranges of adjacent run medians.
            const reference = series.slice(0, 8);
            const limits = [];
            if (reference.length === 8 && series.length > 8 && reference.every(point => point.run.status === 'succeeded')) {
                const mean = reference.reduce((total, point) => total + point.y / 8, 0);
                const mr = reference.slice(1).reduce((total, point, i) => total + Math.abs(point.y - reference[i].y) / 7, 0);
                if (mr > 0 && Number.isFinite(mean + 2.66 * mr) && Number.isFinite(mean - 2.66 * mr)) {
                    limits.push(mean - 2.66 * mr, mean, mean + 2.66 * mr);
                    text(group, 'p', 'Dashed lines show the mean and Individuals-chart limits from the first eight run medians (mean ± 2.66 × mean moving range).', 'note');
                }
            }
            xy(group, 'Benchmark time series', series.map((point, i) => ({
                x: byDate ? point.time : i + 1, y: point.y, label: runMetadata(point.run) + ' · ' + number(point.y)
            })), byDate ? 'Date (UTC)' : 'Run index', first.unit || first.metric, {
                limits, xFormat: byDate ? value => new Date(value).toISOString().slice(0, 16).replace('T', ' ') : number
            });
            table(group, ['Run', 'Started', 'Median', 'Measurements', 'Commit', 'Compiler / generator', 'Host', 'Status'],
                series.map(point => [point.run.id, point.run.startedAt ?? 'Unavailable', number(point.y), number(point.count),
                    point.run.revision ?? 'Unavailable', compilerInfo(point.run) || 'Unavailable',
                    point.run.hostId ?? 'Unavailable', point.run.status]));
        });
        opened = true;
    }
}

function renderBenchmarkCharts() {
    const mode = data.historyMode;
    const comparisonRows = data.detection?.results ?? data.differences;
    const groups = groupBy(comparisonRows ?? measurementRows, metricKey);
    if (!groups.size) {
        if (mode) text(section('benchmarks', 'Benchmark charts'), 'p', 'No measurements match this selection.');
        return;
    }
    const block = section('benchmarks', mode === 'runs' ? 'History time series' : 'Benchmark charts');
    const label = text(block, 'label', 'Benchmark metric');
    label.htmlFor = 'benchmark-metric';
    const select = text(block, 'select');
    select.id = label.htmlFor;
    for (const [key, rows] of groups) {
        const row = rows[0];
        text(select, 'option', [row.metric, row.unit, row.method, row.direction].filter(Boolean).join(' · ')).value = key;
    }
    const display = text(block, 'div');
    function update() {
        display.replaceChildren();
        const rows = groups.get(select.value);
        if (mode === 'compare') historyComparison(display, rows, rows[0]);
        else if (mode === 'detect') historyEffects(display, rows);
        else if (mode === 'runs') timeSeries(display, rows, rows[0]);
        else drawWorkloads(display, rows, mode);
    }
    select.addEventListener('change', update);
    update();
}
renderBenchmarkCharts();
