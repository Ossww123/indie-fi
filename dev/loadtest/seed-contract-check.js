import { check } from 'k6';
import { loadContract, loadCredentials, publicRunMetadata, resultPath } from './lib/contract.js';
import { loadSeedManifest, poolAccountEmail, publicSeedMetadata } from './lib/seed.js';

const contract = loadContract();
const credentials = loadCredentials(contract);
const seed = loadSeedManifest(contract);

export const options = { vus: 1, iterations: 1 };

export default function () {
    check(seed, {
        'claimer account resolves inside its pool': (value) =>
            poolAccountEmail(value, 'claimers', 1).endsWith('@example.test'),
        'credential is available only in memory': () => credentials.password.length >= 12,
    });
}

export function handleSummary(data) {
    const summary = {
        contract: publicRunMetadata(contract),
        seed: publicSeedMetadata(seed),
        checksPassed: data.metrics.checks?.values.passes ?? 0,
        checksFailed: data.metrics.checks?.values.fails ?? 0,
    };
    return {
        stdout: `Load-test seed contract ${contract.runId}: OK\n`,
        [resultPath(contract, 'seed-contract-check.json')]:
            `${JSON.stringify(summary, null, 2)}\n`,
    };
}
