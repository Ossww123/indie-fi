import http from 'k6/http';
import exec from 'k6/execution';
import { Counter, Trend } from 'k6/metrics';
import { loadContract, publicRunMetadata, resultPath } from './lib/contract.js';
import { loadSeedManifest, publicSeedMetadata } from './lib/seed.js';
import { counterValue, rateValue, trendSummary } from './lib/summary.js';

const contract = loadContract();
const seed = loadSeedManifest(contract);
if (contract.scenario !== 'browse') {
    throw new Error('The browse script requires contract.scenario=browse.');
}

const rate = positiveInteger(contract.parameters.ratePerSecond, 'parameters.ratePerSecond');
const duration = positiveInteger(contract.parameters.durationSeconds, 'parameters.durationSeconds');
const preAllocatedVUs = positiveInteger(
    contract.parameters.preAllocatedVUs,
    'parameters.preAllocatedVUs');
const maxVUs = positiveInteger(contract.parameters.maxVUs, 'parameters.maxVUs');
if (maxVUs < preAllocatedVUs) {
    throw new Error('parameters.maxVUs must be at least preAllocatedVUs.');
}

const catalogDuration = new Trend('browse_catalog_duration', true);
const detailDuration = new Trend('browse_detail_duration', true);
const searchDuration = new Trend('browse_search_duration', true);
const filterDuration = new Trend('browse_filter_duration', true);
const cursorDuration = new Trend('browse_cursor_duration', true);
const unexpectedStatus = new Counter('browse_unexpected_status');
const cursorUnavailable = new Counter('browse_cursor_unavailable');
const requestMix = [
    'catalog', 'detail', 'search_hit', 'filter_genre', 'catalog',
    'search_empty', 'detail', 'search_hit', 'filter_tag', 'catalog',
    'detail', 'filter_platform', 'search_hit', 'catalog', 'search_empty',
    'detail', 'filter_genre', 'search_hit', 'catalog', 'cursor_next',
];

export const options = {
    scenarios: {
        catalog: {
            executor: 'constant-arrival-rate',
            exec: 'browseOnce',
            rate,
            timeUnit: '1s',
            duration: `${duration}s`,
            preAllocatedVUs,
            maxVUs,
            gracefulStop: '10s',
            tags: publicTags(),
        },
    },
    thresholds: {
        http_req_failed: [`rate<${contract.thresholds.httpReqFailedRate}`],
        http_req_duration: [`p(95)<${contract.thresholds.httpReqDurationP95Ms}`],
        browse_unexpected_status: ['count==0'],
        dropped_iterations: ['count==0'],
    },
    summaryTrendStats: ['count', 'avg', 'med', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
    const response = request('/', 'setup_catalog');
    if (response.status !== 200) {
        throw new Error(`Catalog setup returned HTTP ${response.status}.`);
    }
    const cursorPath = response.html()
        .find('[data-discovery-next]')
        .first()
        .attr('href') ?? null;
    return { cursorPath };
}

export function browseOnce(data) {
    const operation = requestMix[exec.scenario.iterationInTest % requestMix.length];
    if (operation === 'catalog') {
        record(request('/', 'catalog'), catalogDuration, 'catalog');
    } else if (operation === 'detail') {
        const offset = exec.scenario.iterationInTest % seed.catalog.gameCount;
        const gameId = seed.catalog.gameIds[offset];
        record(request(`/Games/Detail/${gameId}`, 'game_detail'), detailDuration, 'game_detail');
    } else if (operation === 'search_hit') {
        record(request('/?q=Aurora', 'search_hit'), searchDuration, 'search_hit');
    } else if (operation === 'search_empty') {
        record(request('/?q=opv-no-result-token', 'search_empty'), searchDuration, 'search_empty');
    } else if (operation === 'filter_genre') {
        record(request('/?genre=action', 'filter_genre'), filterDuration, 'filter_genre');
    } else if (operation === 'filter_tag') {
        record(request('/?tags=single-player', 'filter_tag'), filterDuration, 'filter_tag');
    } else if (operation === 'filter_platform') {
        record(request('/?platforms=windows', 'filter_platform'), filterDuration, 'filter_platform');
    } else if (data.cursorPath) {
        record(request(data.cursorPath, 'cursor_next'), cursorDuration, 'cursor_next');
    } else {
        cursorUnavailable.add(1);
        unexpectedStatus.add(1, { operation: 'cursor_next' });
    }
}

export function handleSummary(data) {
    const summary = {
        contract: publicRunMetadata(contract),
        seed: publicSeedMetadata(seed),
        generatedAtUtc: new Date().toISOString(),
        result: {
            httpFailureRate: rateValue(data, 'http_req_failed'),
            droppedIterations: counterValue(data, 'dropped_iterations'),
            unexpectedStatus: counterValue(data, 'browse_unexpected_status'),
            cursorUnavailable: counterValue(data, 'browse_cursor_unavailable'),
        },
        durationMilliseconds: {
            catalog: trendSummary(data, 'browse_catalog_duration'),
            detail: trendSummary(data, 'browse_detail_duration'),
            search: trendSummary(data, 'browse_search_duration'),
            filter: trendSummary(data, 'browse_filter_duration'),
            cursor: trendSummary(data, 'browse_cursor_duration'),
        },
    };
    return {
        stdout: consoleSummary(summary),
        [resultPath(contract, 'browse-summary.json')]: `${JSON.stringify(summary, null, 2)}\n`,
    };
}

function request(path, operation) {
    return http.get(`${contract.baseUrl}${path}`, {
        tags: { name: operation, operation },
        responseCallback: http.expectedStatuses(200),
        redirects: 0,
    });
}

function record(response, trend, operation) {
    trend.add(response.timings.duration);
    if (response.status !== 200) {
        unexpectedStatus.add(1, { operation });
    }
}

function positiveInteger(value, name) {
    if (!Number.isInteger(value) || value <= 0) {
        throw new Error(`${name} must be a positive integer.`);
    }
    return value;
}

function publicTags() {
    return {
        run_id: contract.runId,
        load_scenario: contract.scenario,
        load_stage: contract.stage,
        topology: contract.topology,
        commit: contract.commit.slice(0, 12),
    };
}

function consoleSummary(summary) {
    const lines = [
        '',
        `Browse load ${contract.runId} (${contract.stage})`,
        `http failure rate: ${format(summary.result.httpFailureRate)}`,
        `dropped iterations: ${summary.result.droppedIterations}`,
        `unexpected status: ${summary.result.unexpectedStatus}`,
    ];
    for (const [name, trend] of Object.entries(summary.durationMilliseconds)) {
        lines.push(`${name.padEnd(8)} p95=${milliseconds(trend.p95)} p99=${milliseconds(trend.p99)}`);
    }
    return `${lines.join('\n')}\n`;
}

function format(value) {
    return value === null ? '-' : value.toFixed(4);
}

function milliseconds(value) {
    return value === null ? '-' : `${value.toFixed(2)}ms`;
}
