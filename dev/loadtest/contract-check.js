import { check } from 'k6';
import {
    loadContract,
    loadCredentials,
    publicRunMetadata,
    resultPath,
} from './lib/contract.js';

const contract = loadContract();
const credentials = loadCredentials(contract);

export const options = {
    vus: 1,
    iterations: 1,
};

export default function () {
    check(contract, {
        'contract run id is available': (value) => value.runId.length > 0,
        'credentials contain a usable secret': () => credentials.password.length >= 12,
    });
}

export function handleSummary(data) {
    const summary = {
        contract: publicRunMetadata(contract),
        credentialsLoaded: true,
        checksPassed: data.metrics.checks?.values.passes ?? 0,
        checksFailed: data.metrics.checks?.values.fails ?? 0,
    };
    const serialized = `${JSON.stringify(summary, null, 2)}\n`;

    return {
        stdout: `Load-test contract ${contract.runId}: OK\n`,
        [resultPath(contract, 'contract-check.json')]: serialized,
    };
}
