import http from 'k6/http';
import exec from 'k6/execution';
import { Counter, Trend } from 'k6/metrics';
import { sleep } from 'k6';

const configuration = JSON.parse(open('./drop.json'));
const opensAtMilliseconds = Date.parse(configuration.opensAt);

if (!Number.isInteger(configuration.dropId) || configuration.dropId <= 0) {
    throw new Error('dropId must be a positive integer.');
}

if (!Number.isInteger(configuration.claimerCount) || configuration.claimerCount <= 0) {
    throw new Error('claimerCount must be a positive integer.');
}

if (!Number.isInteger(configuration.latecomerCount) || configuration.latecomerCount <= 0) {
    throw new Error('latecomerCount must be a positive integer.');
}

if (Number.isNaN(opensAtMilliseconds)) {
    throw new Error('opensAt must be a valid date.');
}

const baseUrl = configuration.baseUrl.replace(/\/+$/, '');
const secondsUntilOpen = Math.max(
    0,
    Math.ceil((opensAtMilliseconds - Date.now()) / 1000));

// How long after the open moment latecomers keep trickling in.
const LATE_CLAIM_WINDOW_SECONDS = 30;

const granted = new Counter('claim_granted');
const conflict = new Counter('claim_conflict');
const soldOut = new Counter('claim_sold_out');
const notOpen = new Counter('claim_not_open');
const other = new Counter('claim_other');
const claimDuration = new Trend('claim_duration', true);
const browseDuration = new Trend('browse_duration', true);

export const options = {
    scenarios: {
        browsers: {
            executor: 'constant-vus',
            exec: 'browse',
            vus: 50,
            duration: `${Math.max(35, secondsUntilOpen + 35)}s`,
        },
        claimers: {
            executor: 'per-vu-iterations',
            exec: 'claimOnce',
            vus: configuration.claimerCount,
            iterations: 1,
            maxDuration: `${Math.max(120, secondsUntilOpen + 120)}s`,
        },
        // Latecomers also sign in before the drop opens and only then stagger their
        // claims. Signing in during the measured window would burn CPU on password
        // hashing and blur why claim latency moved.
        latecomers: {
            executor: 'per-vu-iterations',
            exec: 'claimLate',
            vus: configuration.latecomerCount,
            iterations: 1,
            maxDuration: `${Math.max(150, secondsUntilOpen + 150)}s`,
        },
    },
    summaryTrendStats: ['med', 'p(95)', 'p(99)'],
};

export function setup() {
    const response = http.get(
        `${baseUrl}/KeyDrops/Detail/${configuration.dropId}`);
    const gamePath = response.html()
        .find('a[href^="/Games/Detail/"]')
        .first()
        .attr('href');

    if (!gamePath) {
        throw new Error(
            `Could not find the game link for drop ${configuration.dropId}.`);
    }

    return { gamePath };
}

export function browse(data) {
    browseGet('/');
    sleep(randomBrowsePause());
    browseGet(data.gamePath);
    sleep(randomBrowsePause());
    browseGet(`/KeyDrops/Detail/${configuration.dropId}`);
    sleep(randomBrowsePause());
}

export function claimOnce() {
    const accountNumber = exec.scenario.iterationInTest + 1;
    claimWithAccount(accountNumber, 0);
}

export function claimLate() {
    // Accounts after the claimer block, so a latecomer never reuses an account that
    // already holds a key: that returns AlreadyClaimed, which the page renders like
    // a fresh grant and would be counted as a success.
    const accountNumber =
        configuration.claimerCount + exec.scenario.iterationInTest + 1;
    claimWithAccount(accountNumber, Math.random() * LATE_CLAIM_WINDOW_SECONDS);
}

function claimWithAccount(accountNumber, extraWaitSeconds) {
    http.cookieJar().clear(baseUrl);

    const loginPage = http.get(`${baseUrl}/Login`);
    const loginToken = antiforgeryToken(loginPage);

    if (!loginToken) {
        other.add(1);
        return;
    }

    http.post(
        `${baseUrl}/Login`,
        {
            Email: loadTestEmail(accountNumber),
            Password: configuration.password,
            __RequestVerificationToken: loginToken,
        });

    const detailUrl =
        `${baseUrl}/KeyDrops/Detail/${configuration.dropId}`;
    const detailPage = http.get(detailUrl);
    const claimToken = antiforgeryToken(detailPage);

    if (!claimToken) {
        other.add(1);
        return;
    }

    waitUntilOpen();

    if (extraWaitSeconds > 0) {
        sleep(extraWaitSeconds);
    }

    const startedAt = Date.now();
    const response = http.post(
        `${detailUrl}?handler=Claim`,
        { __RequestVerificationToken: claimToken });
    claimDuration.add(Date.now() - startedAt);

    classifyClaim(response.body);
}

function antiforgeryToken(response) {
    if (typeof response.body !== 'string') {
        return null;
    }

    return response.html()
        .find('input[name="__RequestVerificationToken"]')
        .first()
        .attr('value');
}

function waitUntilOpen() {
    const remainingMilliseconds = opensAtMilliseconds - Date.now();

    if (remainingMilliseconds > 0) {
        sleep(remainingMilliseconds / 1000);
    }
}

function classifyClaim(html) {
    if (typeof html !== 'string') {
        other.add(1);
    } else if (html.includes('받은 키:')) {
        granted.add(1);
    } else if (html.includes('동시 요청으로')) {
        conflict.add(1);
    } else if (html.includes('키가 모두 소진되었습니다')) {
        soldOut.add(1);
    } else if (html.includes('아직 오픈되지 않았습니다')) {
        notOpen.add(1);
    } else {
        other.add(1);
    }
}

function browseGet(path) {
    const response = http.get(absoluteUrl(path));
    browseDuration.add(response.timings.duration);
}

function absoluteUrl(path) {
    return path.startsWith('http') ? path : `${baseUrl}${path}`;
}

function randomBrowsePause() {
    return 1 + Math.random();
}

function loadTestEmail(accountNumber) {
    return `loadtest-${String(accountNumber).padStart(4, '0')}@example.test`;
}

export function handleSummary(data) {
    const outcomes = [
        outcome('granted', counterValue(data, 'claim_granted')),
        outcome('conflict', counterValue(data, 'claim_conflict')),
        outcome('soldout', counterValue(data, 'claim_sold_out')),
        outcome('notopen', counterValue(data, 'claim_not_open')),
        outcome('other', counterValue(data, 'claim_other')),
    ];
    const total = outcomes.reduce((sum, item) => sum + item.count, 0);

    for (const item of outcomes) {
        item.percentage = total === 0 ? 0 : (item.count / total) * 100;
    }

    const summary = {
        dropId: configuration.dropId,
        opensAt: configuration.opensAt,
        totalClaims: total,
        outcomes,
        durationsMilliseconds: {
            claim: {
                p50: trendValue(data, 'claim_duration', 'med'),
                p95: trendValue(data, 'claim_duration', 'p(95)'),
                p99: trendValue(data, 'claim_duration', 'p(99)'),
            },
            browse: {
                p95: trendValue(data, 'browse_duration', 'p(95)'),
            },
        },
        state: data.state,
        metrics: data.metrics,
    };

    return {
        stdout: consoleSummary(summary),
        'dev/loadtest/results/last-run.json':
            `${JSON.stringify(summary, null, 2)}\n`,
    };
}

function counterValue(data, metricName) {
    return data.metrics[metricName]?.values.count ?? 0;
}

function trendValue(data, metricName, statistic) {
    return data.metrics[metricName]?.values[statistic] ?? null;
}

function outcome(name, count) {
    return { name, count, percentage: 0 };
}

function consoleSummary(summary) {
    const lines = [
        '',
        'Key claim results',
        'outcome       count      ratio',
        '-----------  ------  ---------',
    ];

    for (const item of summary.outcomes) {
        lines.push(
            `${item.name.padEnd(11)}  ${String(item.count).padStart(6)}  ` +
            `${`${item.percentage.toFixed(2)}%`.padStart(9)}`);
    }

    lines.push('');
    lines.push(
        `claim p50  ${formatMilliseconds(summary.durationsMilliseconds.claim.p50)}`);
    lines.push(
        `claim p95  ${formatMilliseconds(summary.durationsMilliseconds.claim.p95)}`);
    lines.push(
        `claim p99  ${formatMilliseconds(summary.durationsMilliseconds.claim.p99)}`);
    lines.push(
        `browse p95 ${formatMilliseconds(summary.durationsMilliseconds.browse.p95)}`);
    lines.push('');

    return `${lines.join('\n')}\n`;
}

function formatMilliseconds(value) {
    return value === null ? '-' : `${value.toFixed(2)} ms`;
}
