import http from 'k6/http';
import exec from 'k6/execution';
import { Counter, Trend } from 'k6/metrics';
import { sleep } from 'k6';
import {
    loadContract,
    loadCredentials,
    publicRunMetadata,
    resultPath,
} from './lib/contract.js';
import {
    loadSeedManifest,
    poolAccountEmail,
    publicSeedMetadata,
} from './lib/seed.js';
import { counterValue, rateValue, trendSummary } from './lib/summary.js';

const contract = loadContract();
if (contract.scenario !== 'keydrop') {
    throw new Error('contract.scenario must be keydrop for this script.');
}

const credentials = loadCredentials(contract);
const seed = loadSeedManifest(contract);
const opensAtMilliseconds = Date.parse(seed.drop.opensAtUtc);
const secondsUntilOpen = Math.ceil((opensAtMilliseconds - Date.now()) / 1000);
const parameters = validateParameters(contract.parameters);

const prepareFailed = new Counter('keydrop_prepare_failed');
const snapshotFailed = new Counter('waiting_snapshot_failed');
const waitingTerminal = new Counter('waiting_terminal');
const waitingUnavailable = new Counter('waiting_unavailable');
const granted = new Counter('claim_granted');
const alreadyClaimed = new Counter('claim_already_claimed');
const soldOut = new Counter('claim_sold_out');
const conflict = new Counter('claim_conflict');
const notOpen = new Counter('claim_not_open');
const admissionRequired = new Counter('claim_admission_required');
const claimWaitingTerminal = new Counter('claim_waiting_terminal');
const claimWaitingUnavailable = new Counter('claim_waiting_unavailable');
const other = new Counter('claim_other');
const snapshotDuration = new Trend('waiting_snapshot_duration', true);
const admissionWaitDuration = new Trend('admission_wait_duration', true);
const lateJoinDuration = new Trend('late_join_duration', true);
const claimDuration = new Trend('claim_duration', true);

const scenarios = {};
addPoolScenario(scenarios, 'claimers', 'claimOnce', seed.accounts.claimers.count);
addPoolScenario(scenarios, 'latecomers', 'claimLate', seed.accounts.latecomers.count);
addPoolScenario(scenarios, 'retries', 'claimRetry', seed.accounts.retries.count);
if (Object.keys(scenarios).length === 0) {
    throw new Error('At least one claimer, latecomer, or retry account is required.');
}

export const options = {
    scenarios,
    thresholds: {
        'http_req_failed{phase:measurement}': [
            `rate<${contract.thresholds.httpReqFailedRate}`,
        ],
        'http_req_duration{phase:measurement}': [
            `p(95)<${contract.thresholds.httpReqDurationP95Ms}`,
        ],
        keydrop_prepare_failed: ['count==0'],
        waiting_snapshot_failed: ['count==0'],
        waiting_unavailable: ['count==0'],
        claim_conflict: ['count==0'],
        claim_not_open: ['count==0'],
        claim_waiting_unavailable: ['count==0'],
        claim_other: ['count==0'],
    },
    summaryTrendStats: ['count', 'avg', 'med', 'p(95)', 'p(99)', 'max'],
};

export function claimOnce() {
    runParticipant('claimers', false, false);
}

export function claimLate() {
    runParticipant('latecomers', true, false);
}

export function claimRetry() {
    runParticipant('retries', false, true);
}

function runParticipant(poolName, joinsAfterOpen, retriesClaim) {
    const number = exec.scenario.iterationInTest + 1;
    spreadPreparation(number);
    http.cookieJar().clear(contract.baseUrl);

    if (!login(poolName, number)) {
        prepareFailed.add(1);
        return;
    }

    const detailUrl = `${contract.baseUrl}/KeyDrops/Detail/${seed.drop.id}`;
    let claimToken;
    if (!joinsAfterOpen) {
        claimToken = joinWaitingRoom(detailUrl, 'preparation');
        if (claimToken === false) {
            return;
        }
        if (claimToken === null) {
            prepareFailed.add(1);
            return;
        }
    }

    waitUntilOpen();
    if (joinsAfterOpen) {
        sleep(lateDelaySeconds(number));
        const startedAt = Date.now();
        claimToken = joinWaitingRoom(detailUrl, 'measurement');
        lateJoinDuration.add(Date.now() - startedAt);
        if (claimToken === false) {
            return;
        }
        if (claimToken === null) {
            other.add(1);
            return;
        }
    }

    const admissionStartedAt = Date.now();
    if (!waitForAdmission(detailUrl)) {
        return;
    }
    admissionWaitDuration.add(Date.now() - admissionStartedAt);

    const firstOutcome = submitClaim(detailUrl, claimToken, false);
    if (retriesClaim && firstOutcome === 'granted') {
        submitClaim(detailUrl, claimToken, true);
    }
}

function login(poolName, number) {
    const loginPage = http.get(`${contract.baseUrl}/Login`, requestOptions(
        'GET /Login [preparation]', 'preparation', http.expectedStatuses(200)));
    const token = antiforgeryToken(loginPage);
    if (!token) {
        return false;
    }

    const response = http.post(
        `${contract.baseUrl}/Login`,
        {
            Email: poolAccountEmail(seed, poolName, number),
            Password: credentials.password,
            __RequestVerificationToken: token,
        },
        requestOptions('POST /Login [preparation]', 'preparation', http.expectedStatuses(200)));

    return response.status === 200 && !response.url.includes('/Login');
}

function joinWaitingRoom(detailUrl, phase) {
    const detail = http.get(
        detailUrl,
        requestOptions(`GET /KeyDrops/Detail/:id [${phase}]`, phase, http.expectedStatuses(200)));
    const token = antiforgeryToken(detail);
    if (!token) {
        return null;
    }

    const join = http.post(
        `${detailUrl}?handler=Join`,
        { __RequestVerificationToken: token },
        requestOptions(`POST /KeyDrops/Detail/:id?handler=Join [${phase}]`, phase,
            http.expectedStatuses(200, 302)));
    if (join.status !== 200) {
        return null;
    }

    const waitingCode = queryCode(join.url, 'waiting');
    if (waitingCode && !['joined', 'existing', 'rejoined'].includes(waitingCode)) {
        classifyWaitingTerminal(waitingCode);
        return false;
    }
    return token;
}

function waitForAdmission(detailUrl) {
    const deadline = Date.now() + parameters.admissionTimeoutSeconds * 1000;
    while (Date.now() < deadline) {
        const startedAt = Date.now();
        const response = http.get(
            `${detailUrl}?handler=WaitingRoomSnapshot`,
            requestOptions('GET /KeyDrops/Detail/:id?handler=WaitingRoomSnapshot',
                'measurement', http.expectedStatuses(200, 503)));
        snapshotDuration.add(Date.now() - startedAt);

        if (response.status === 503) {
            waitingUnavailable.add(1);
            return false;
        }
        if (response.status !== 200) {
            snapshotFailed.add(1);
            return false;
        }

        let snapshot;
        try {
            snapshot = response.json();
        } catch (_) {
            snapshotFailed.add(1);
            return false;
        }

        if (snapshot.entryState === 'Admitted') {
            return true;
        }
        if (isTerminalSnapshot(snapshot)) {
            waitingTerminal.add(1);
            return false;
        }
        sleep(parameters.pollIntervalMilliseconds / 1000);
    }

    snapshotFailed.add(1);
    return false;
}

function submitClaim(detailUrl, token, retryAttempt) {
    const startedAt = Date.now();
    const response = http.post(
        `${detailUrl}?handler=Claim`,
        { __RequestVerificationToken: token },
        requestOptions('POST /KeyDrops/Detail/:id?handler=Claim', 'measurement',
            http.expectedStatuses(200, 302)));
    claimDuration.add(Date.now() - startedAt);

    const outcome = classifyClaim(response, retryAttempt);
    return outcome;
}

function classifyClaim(response, retryAttempt) {
    const code = queryCode(response.url, 'claim');
    if (typeof response.body === 'string'
        && response.body.includes('data-claim-result="granted"')) {
        if (retryAttempt) {
            alreadyClaimed.add(1);
            return 'already_claimed';
        }
        granted.add(1);
        return 'granted';
    }

    switch (code) {
        case 'soldout': soldOut.add(1); return 'sold_out';
        case 'conflict': conflict.add(1); return 'conflict';
        case 'notopen': notOpen.add(1); return 'not_open';
        case 'admission': admissionRequired.add(1); return 'admission_required';
        case 'waitingclosed': claimWaitingTerminal.add(1); return 'waiting_terminal';
        case 'waitingunavailable': claimWaitingUnavailable.add(1); return 'waiting_unavailable';
        default: other.add(1); return 'other';
    }
}

function isTerminalSnapshot(snapshot) {
    return ['Completed', 'SoldOut', 'ClosedEarly', 'Terminal']
        .includes(snapshot.participationOutcome)
        || ['Completed', 'Terminal'].includes(snapshot.entryState)
        || ['SoldOut', 'ClosedEarly', 'Ended'].includes(snapshot.dropStatus);
}

function classifyWaitingTerminal(code) {
    if (code === 'unavailable') {
        waitingUnavailable.add(1);
    } else {
        waitingTerminal.add(1);
    }
}

function requestOptions(name, phase, responseCallback) {
    return {
        tags: { name, phase },
        responseCallback,
    };
}

function antiforgeryToken(response) {
    if (response.status !== 200 || typeof response.body !== 'string') {
        return null;
    }
    return response.html()
        .find('input[name="__RequestVerificationToken"]')
        .first()
        .attr('value');
}

function queryCode(url, name) {
    const match = new RegExp(`[?&]${name}=([^&#]+)`, 'i').exec(url ?? '');
    return match ? decodeURIComponent(match[1]).toLowerCase() : null;
}

function waitUntilOpen() {
    const milliseconds = opensAtMilliseconds - Date.now();
    if (milliseconds > 0) {
        sleep(milliseconds / 1000);
    }
}

function spreadPreparation(number) {
    if (parameters.preparationSpreadSeconds <= 0) {
        return;
    }
    const fraction = ((number * 7919) % 1000) / 1000;
    sleep(fraction * parameters.preparationSpreadSeconds);
}

function lateDelaySeconds(number) {
    if (parameters.lateWindowSeconds <= 0) {
        return 0;
    }
    const fraction = ((number * 3571) % 1000) / 1000;
    return fraction * parameters.lateWindowSeconds;
}

function addPoolScenario(target, name, executorName, count) {
    if (count === 0) {
        return;
    }
    target[name] = {
        executor: 'per-vu-iterations',
        exec: executorName,
        vus: count,
        iterations: 1,
        maxDuration: `${Math.max(
            1,
            secondsUntilOpen + parameters.admissionTimeoutSeconds + 120)}s`,
        tags: {
            run_id: contract.runId,
            scenario: contract.scenario,
            stage: contract.stage,
            topology: contract.topology,
            pool: name,
        },
    };
}

function validateParameters(value) {
    positiveInteger(value.pollIntervalMilliseconds, 'pollIntervalMilliseconds');
    if (value.pollIntervalMilliseconds < 250 || value.pollIntervalMilliseconds > 15000) {
        throw new Error('pollIntervalMilliseconds must be between 250 and 15000.');
    }
    positiveInteger(value.admissionTimeoutSeconds, 'admissionTimeoutSeconds');
    nonNegativeNumber(value.lateWindowSeconds, 'lateWindowSeconds');
    nonNegativeNumber(value.preparationSpreadSeconds, 'preparationSpreadSeconds');
    if (secondsUntilOpen > 0 && value.preparationSpreadSeconds >= secondsUntilOpen) {
        throw new Error('preparationSpreadSeconds must be shorter than the time until open.');
    }
    return Object.freeze(value);
}

function positiveInteger(value, name) {
    if (!Number.isInteger(value) || value <= 0) {
        throw new Error(`contract.parameters.${name} must be a positive integer.`);
    }
}

function nonNegativeNumber(value, name) {
    if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) {
        throw new Error(`contract.parameters.${name} must be a non-negative number.`);
    }
}

export function handleSummary(data) {
    const summary = {
        run: publicRunMetadata(contract),
        seed: publicSeedMetadata(seed),
        outcomes: {
            granted: counterValue(data, 'claim_granted'),
            alreadyClaimed: counterValue(data, 'claim_already_claimed'),
            soldOut: counterValue(data, 'claim_sold_out'),
            conflict: counterValue(data, 'claim_conflict'),
            notOpen: counterValue(data, 'claim_not_open'),
            admissionRequired: counterValue(data, 'claim_admission_required'),
            waitingTerminal: counterValue(data, 'waiting_terminal'),
            waitingUnavailable: counterValue(data, 'waiting_unavailable'),
            claimWaitingTerminal: counterValue(data, 'claim_waiting_terminal'),
            claimWaitingUnavailable: counterValue(data, 'claim_waiting_unavailable'),
            preparationFailed: counterValue(data, 'keydrop_prepare_failed'),
            snapshotFailed: counterValue(data, 'waiting_snapshot_failed'),
            other: counterValue(data, 'claim_other'),
        },
        durationsMilliseconds: {
            waitingSnapshot: trendSummary(data, 'waiting_snapshot_duration'),
            admissionWait: trendSummary(data, 'admission_wait_duration'),
            lateJoin: trendSummary(data, 'late_join_duration'),
            claim: trendSummary(data, 'claim_duration'),
        },
        measurementHttpFailureRate:
            rateValue(data, 'http_req_failed{phase:measurement}'),
        thresholdFailures: Object.entries(data.metrics)
            .filter(([, metric]) => metric.thresholds
                && Object.values(metric.thresholds).some((threshold) => !threshold.ok))
            .map(([name]) => name),
    };

    return {
        stdout: consoleSummary(summary),
        [resultPath(contract, 'keydrop-summary.json')]:
            `${JSON.stringify(summary, null, 2)}\n`,
    };
}

function consoleSummary(summary) {
    const o = summary.outcomes;
    return [
        '',
        `run: ${summary.run.runId}`,
        `fresh grants: ${o.granted}`,
        `idempotent retries: ${o.alreadyClaimed}`,
        `sold out / waiting terminal: ${o.soldOut} / ${o.waitingTerminal}`,
        `conflict / other: ${o.conflict} / ${o.other}`,
        `claim p95: ${formatMilliseconds(summary.durationsMilliseconds.claim.p95)}`,
        `threshold failures: ${summary.thresholdFailures.length === 0
            ? 'none'
            : summary.thresholdFailures.join(', ')}`,
        '',
    ].join('\n');
}

function formatMilliseconds(value) {
    return value === null ? '-' : `${value.toFixed(2)} ms`;
}
