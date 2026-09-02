import http from 'k6/http';
import exec from 'k6/execution';
import encoding from 'k6/encoding';
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
import { counterValue, trendSummary } from './lib/summary.js';

const contract = loadContract();
if (contract.scenario !== 'keydrop' || contract.topology !== 'multi') {
    throw new Error('worker competition requires a multi-topology keydrop contract.');
}

const credentials = loadCredentials(contract);
const seed = loadSeedManifest(contract);
const mediaAccountCount = integerEnvironment(
    'MEDIA_ACCOUNT_COUNT',
    20,
    1,
    seed.accounts.claimers.count);
const preparationSpreadSeconds = integerEnvironment(
    'PREPARATION_SPREAD_SECONDS', 60, 0, 300);
const mediaWaveSeconds = integerEnvironment('MEDIA_WAVE_SECONDS', 15, 1, 60);
const mediaReadyTimeoutSeconds = integerEnvironment(
    'MEDIA_READY_TIMEOUT_SECONDS', 180, 30, 600);
const actionAtMilliseconds = Date.parse(requiredEnvironment('ACTION_AT_UTC'));
if (!Number.isFinite(actionAtMilliseconds) || actionAtMilliseconds <= Date.now()) {
    throw new Error('ACTION_AT_UTC must be a future ISO-8601 timestamp.');
}

const png = encoding.b64decode(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
    'std');
const pngLength = 68;
if (png.byteLength !== pngLength) {
    throw new Error('The embedded validation PNG has an unexpected length.');
}

const preparationFailed = new Counter('worker_batch_preparation_failed');
const preferencesSaved = new Counter('worker_batch_preferences_saved');
const subscriptionsCreated = new Counter('worker_batch_subscriptions_created');
const mediaRequested = new Counter('worker_batch_media_requested');
const mediaReady = new Counter('worker_batch_media_ready');
const mediaFailed = new Counter('worker_batch_media_failed');
const mediaTerminalDuration = new Trend('worker_batch_media_terminal_duration', true);

const secondsUntilLastWave = Math.ceil(
    (actionAtMilliseconds - Date.now()) / 1000)
    + (3 * mediaWaveSeconds)
    + mediaReadyTimeoutSeconds
    + 30;

export const options = {
    scenarios: {
        participants: {
            executor: 'per-vu-iterations',
            exec: 'prepareAndCreateWork',
            vus: seed.accounts.claimers.count,
            iterations: 1,
            maxDuration: `${Math.max(60, secondsUntilLastWave)}s`,
            gracefulStop: '15s',
            tags: {
                run_id: contract.runId,
                scenario: 'worker-competition',
                stage: contract.stage,
                topology: contract.topology,
            },
        },
    },
    thresholds: {
        worker_batch_preparation_failed: ['count==0'],
        worker_batch_media_failed: ['count==0'],
    },
    systemTags: ['status', 'method', 'name', 'scenario', 'expected_response'],
    summaryTrendStats: ['count', 'avg', 'med', 'p(95)', 'p(99)', 'max'],
};

export function prepareAndCreateWork() {
    const number = exec.scenario.iterationInTest + 1;
    spreadPreparation(number);
    http.cookieJar().clear(contract.baseUrl);

    if (!login(number) || !disableOptionalEmail() || !subscribeToDrop()) {
        preparationFailed.add(1);
        return;
    }

    if (number > mediaAccountCount) {
        return;
    }

    const wave = (number - 1) % 4;
    waitUntil(actionAtMilliseconds + (wave * mediaWaveSeconds * 1000));
    createProfileImageWork();
}

function login(number) {
    const loginPage = http.get(
        `${contract.baseUrl}/Login`,
        requestOptions('GET /Login [worker preparation]', 200));
    const token = pageAntiforgeryToken(loginPage);
    if (!token) {
        return false;
    }

    const response = http.post(
        `${contract.baseUrl}/Login`,
        {
            Email: poolAccountEmail(seed, 'claimers', number),
            Password: credentials.password,
            __RequestVerificationToken: token,
        },
        requestOptions('POST /Login [worker preparation]', 200));
    return response.status === 200 && !response.url.includes('/Login');
}

function disableOptionalEmail() {
    const page = http.get(
        `${contract.baseUrl}/Account/Notifications`,
        requestOptions('GET /Account/Notifications [worker preparation]', 200));
    const token = pageAntiforgeryToken(page);
    if (!token) {
        return false;
    }

    const form = { __RequestVerificationToken: token };
    for (let index = 0; index < 9; index += 1) {
        form[`Preferences[${index}].Kind`] = String(index + 1);
        form[`Preferences[${index}].WebEnabled`] = 'true';
        form[`Preferences[${index}].EmailEnabled`] = 'false';
    }

    const response = http.post(
        `${contract.baseUrl}/Account/Notifications`,
        form,
        requestOptions('POST /Account/Notifications [worker preparation]', 200));
    const saved = response.status === 200
        && typeof response.body === 'string'
        && response.body.includes('알림 설정을 저장했습니다.');
    if (saved) {
        preferencesSaved.add(1);
    }
    return saved;
}

function subscribeToDrop() {
    const detailUrl = `${contract.baseUrl}/KeyDrops/Detail/${seed.drop.id}`;
    const page = http.get(
        detailUrl,
        requestOptions('GET /KeyDrops/Detail/:id [worker preparation]', 200));
    const token = pageAntiforgeryToken(page);
    if (!token) {
        return false;
    }

    const response = http.post(
        `${detailUrl}?handler=SubscribeNotification`,
        { __RequestVerificationToken: token },
        requestOptions(
            'POST /KeyDrops/Detail/:id?handler=SubscribeNotification [worker preparation]',
            200));
    const subscribed = response.status === 200
        && response.url.includes('notification=subscribed');
    if (subscribed) {
        subscriptionsCreated.add(1);
    }
    return subscribed;
}

function createProfileImageWork() {
    const profile = http.get(
        `${contract.baseUrl}/Account/Profile`,
        requestOptions('GET /Account/Profile [worker workload]', 200));
    const targetId = Number(profile.html()
        .find('[data-profile-media]')
        .first()
        .attr('data-profile-user-id'));
    if (!Number.isSafeInteger(targetId) || targetId <= 0) {
        mediaFailed.add(1);
        return;
    }

    const antiforgeryResponse = http.get(
        `${contract.baseUrl}/api/media/antiforgery`,
        requestOptions('GET /api/media/antiforgery [worker workload]', 200));
    if (antiforgeryResponse.status !== 200) {
        mediaFailed.add(1);
        return;
    }
    const antiforgery = antiforgeryResponse.json();
    if (!antiforgery.headerName || !antiforgery.requestToken) {
        mediaFailed.add(1);
        return;
    }

    const reservation = sendJson(
        '/api/media/reservations',
        'POST',
        {
            targetKind: 'Profile',
            targetId,
            attachmentKind: 'ProfileImage',
            contentType: 'image/png',
            contentLength: pngLength,
        },
        antiforgery,
        'POST /api/media/reservations [worker workload]',
        201);
    if (reservation.status !== 201) {
        mediaFailed.add(1);
        return;
    }
    const reserved = reservation.json();

    const attemptResponse = sendJson(
        `/api/media/${encodeURIComponent(reserved.mediaId)}/upload-attempts`,
        'POST',
        undefined,
        antiforgery,
        'POST /api/media/:id/upload-attempts [worker workload]',
        201);
    if (attemptResponse.status !== 201) {
        mediaFailed.add(1);
        return;
    }
    const attempt = attemptResponse.json();
    if (attempt.requiredContentType !== 'image/png'
        || attempt.requiredContentLength !== pngLength) {
        mediaFailed.add(1);
        return;
    }

    const upload = http.put(attempt.uploadUrl, png, {
        headers: { 'Content-Type': 'image/png' },
        redirects: 0,
        responseCallback: http.expectedStatuses(200),
        tags: { name: 'PUT object storage [worker workload]' },
    });
    if (upload.status !== 200) {
        mediaFailed.add(1);
        return;
    }

    const completion = sendJson(
        `/api/media/${encodeURIComponent(reserved.mediaId)}`
            + `/upload-attempts/${encodeURIComponent(attempt.uploadAttemptId)}/complete`,
        'POST',
        undefined,
        antiforgery,
        'POST /api/media/:id/upload-attempts/:attempt/complete [worker workload]',
        202);
    if (completion.status !== 202) {
        mediaFailed.add(1);
        return;
    }
    mediaRequested.add(1);

    const startedAt = Date.now();
    const deadline = startedAt + (mediaReadyTimeoutSeconds * 1000);
    while (Date.now() < deadline) {
        const statusResponse = http.get(
            `${contract.baseUrl}/api/media/${encodeURIComponent(reserved.mediaId)}`,
            requestOptions('GET /api/media/:id [worker workload]', 200));
        if (statusResponse.status !== 200) {
            mediaFailed.add(1);
            return;
        }
        const status = statusResponse.json().status;
        if (status === 'Ready') {
            mediaReady.add(1);
            mediaTerminalDuration.add(Date.now() - startedAt);
            return;
        }
        if (status === 'Failed' || status === 'Expired') {
            mediaFailed.add(1);
            mediaTerminalDuration.add(Date.now() - startedAt);
            return;
        }
        sleep(0.5);
    }
    mediaFailed.add(1);
    mediaTerminalDuration.add(Date.now() - startedAt);
}

function sendJson(path, method, body, antiforgery, name, expectedStatus) {
    const headers = {
        Accept: 'application/json',
        [antiforgery.headerName]: antiforgery.requestToken,
    };
    let payload = null;
    if (body !== undefined) {
        headers['Content-Type'] = 'application/json';
        payload = JSON.stringify(body);
    }
    return http.request(method, `${contract.baseUrl}${path}`, payload, {
        headers,
        responseCallback: http.expectedStatuses(expectedStatus),
        tags: { name },
    });
}

function requestOptions(name, expectedStatus) {
    return {
        responseCallback: http.expectedStatuses(expectedStatus),
        tags: { name },
    };
}

function pageAntiforgeryToken(response) {
    if (response.status !== 200 || typeof response.body !== 'string') {
        return null;
    }
    return response.html()
        .find('input[name="__RequestVerificationToken"]')
        .first()
        .attr('value');
}

function waitUntil(timestamp) {
    const delayMilliseconds = timestamp - Date.now();
    if (delayMilliseconds > 0) {
        sleep(delayMilliseconds / 1000);
    }
}

function spreadPreparation(number) {
    if (preparationSpreadSeconds === 0) {
        return;
    }
    const fraction = ((number * 7919) % 1000) / 1000;
    sleep(fraction * preparationSpreadSeconds);
}

function requiredEnvironment(name) {
    const value = __ENV[name];
    if (typeof value !== 'string' || value.trim() === '') {
        throw new Error(`${name} is required.`);
    }
    return value;
}

function integerEnvironment(name, fallback, minimum, maximum) {
    const raw = __ENV[name];
    const value = raw === undefined ? fallback : Number(raw);
    if (!Number.isInteger(value) || value < minimum || value > maximum) {
        throw new Error(`${name} must be an integer between ${minimum} and ${maximum}.`);
    }
    return value;
}

export function handleSummary(data) {
    const summary = {
        run: publicRunMetadata(contract),
        seed: publicSeedMetadata(seed),
        workload: {
            participants: seed.accounts.claimers.count,
            mediaAccounts: mediaAccountCount,
            mediaWaves: 4,
            mediaWaveSeconds,
        },
        outcomes: {
            preferencesSaved: counterValue(data, 'worker_batch_preferences_saved'),
            subscriptionsCreated: counterValue(data, 'worker_batch_subscriptions_created'),
            mediaRequested: counterValue(data, 'worker_batch_media_requested'),
            mediaReady: counterValue(data, 'worker_batch_media_ready'),
            mediaFailed: counterValue(data, 'worker_batch_media_failed'),
            preparationFailed: counterValue(data, 'worker_batch_preparation_failed'),
        },
        mediaTerminalDurationMilliseconds:
            trendSummary(data, 'worker_batch_media_terminal_duration'),
        thresholdFailures: Object.entries(data.metrics)
            .filter(([, metric]) => metric.thresholds
                && Object.values(metric.thresholds).some((threshold) => !threshold.ok))
            .map(([name]) => name),
    };

    return {
        stdout: consoleSummary(summary),
        [resultPath(contract, 'worker-competition-summary.json')]:
            `${JSON.stringify(summary, null, 2)}\n`,
    };
}

function consoleSummary(summary) {
    const outcome = summary.outcomes;
    return [
        '',
        `run: ${summary.run.runId}`,
        `preferences / subscriptions: ${outcome.preferencesSaved} / ${outcome.subscriptionsCreated}`,
        `media requested / ready / failed: ${outcome.mediaRequested} / ${outcome.mediaReady} / ${outcome.mediaFailed}`,
        `preparation failures: ${outcome.preparationFailed}`,
        `media terminal p95: ${formatMilliseconds(summary.mediaTerminalDurationMilliseconds.p95)}`,
        `threshold failures: ${summary.thresholdFailures.length === 0 ? 'none' : summary.thresholdFailures.join(', ')}`,
        '',
    ].join('\n');
}

function formatMilliseconds(value) {
    return value === null ? 'n/a' : `${Number(value).toFixed(2)} ms`;
}
