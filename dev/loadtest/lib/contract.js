const runIdPattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;
const stagePattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$/;
const relativeDirectoryPattern = /^[A-Za-z0-9_./-]+$/;
const allowedPurposes = ['calibration', 'baseline', 'improvement'];
const allowedScenarios = ['contract-check', 'browse', 'keydrop'];
const allowedTopologies = ['single', 'multi'];

export function loadContract() {
    const path = requiredEnvironment('CONTRACT_FILE');
    const contract = readJson(path, 'load-test contract');

    requireEqual(contract.schemaVersion, 1, 'contract.schemaVersion');
    requirePattern(contract.runId, runIdPattern, 'contract.runId');
    requireMember(contract.purpose, allowedPurposes, 'contract.purpose');
    validateBaseUrl(contract.baseUrl);
    requireMember(contract.scenario, allowedScenarios, 'contract.scenario');
    requirePattern(contract.stage, stagePattern, 'contract.stage');
    requireMember(contract.topology, allowedTopologies, 'contract.topology');
    requirePattern(contract.commit, /^[a-f0-9]{40}$/, 'contract.commit');
    validateOutputDirectory(contract.outputDirectory);

    const thresholds = requireObject(contract.thresholds, 'contract.thresholds');
    requireRate(thresholds.httpReqFailedRate, 'contract.thresholds.httpReqFailedRate');
    requirePositiveNumber(
        thresholds.httpReqDurationP95Ms,
        'contract.thresholds.httpReqDurationP95Ms');
    requireObject(contract.parameters, 'contract.parameters');

    return Object.freeze({
        ...contract,
        baseUrl: contract.baseUrl.replace(/\/+$/, ''),
    });
}

export function loadCredentials(contract) {
    const path = requiredEnvironment('CREDENTIALS_FILE');
    const credentials = readJson(path, 'load-test credentials');

    requireEqual(credentials.schemaVersion, 1, 'credentials.schemaVersion');
    if (typeof credentials.password !== 'string' || credentials.password.length < 12) {
        throw new Error('credentials.password must contain at least 12 characters.');
    }

    return Object.freeze({
        password: credentials.password,
    });
}

export function publicRunMetadata(contract) {
    return {
        schemaVersion: contract.schemaVersion,
        runId: contract.runId,
        purpose: contract.purpose,
        scenario: contract.scenario,
        stage: contract.stage,
        topology: contract.topology,
        commit: contract.commit,
        thresholds: contract.thresholds,
        parameters: contract.parameters,
    };
}

export function resultPath(contract, fileName) {
    requirePattern(fileName, /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/, 'result file name');
    return `${contract.outputDirectory}/${contract.runId}/${fileName}`;
}

function requiredEnvironment(name) {
    const value = __ENV[name];
    if (typeof value !== 'string' || value.trim() === '') {
        throw new Error(`${name} is required.`);
    }
    return value;
}

function readJson(path, description) {
    try {
        return JSON.parse(open(import.meta.resolve(path)));
    } catch (error) {
        throw new Error(`Could not read ${description}: ${error.message}`);
    }
}

function validateBaseUrl(value) {
    if (typeof value !== 'string' || !/^https?:\/\/[^/\s]+(?:\/)?$/.test(value)) {
        throw new Error('contract.baseUrl must be an origin without a path.');
    }

    const isHttp = value.startsWith('http://');
    const isLoopback = /^http:\/\/(localhost|127(?:\.[0-9]{1,3}){3})(?::[0-9]+)?\/?$/.test(value);
    if (isHttp && !isLoopback) {
        throw new Error('contract.baseUrl must use HTTPS unless the target is loopback.');
    }
}

function validateOutputDirectory(value) {
    requirePattern(value, relativeDirectoryPattern, 'contract.outputDirectory');
    if (value.startsWith('/') || value.includes('..') || value.includes('//')) {
        throw new Error('contract.outputDirectory must be a normalized relative directory.');
    }
}

function requireObject(value, name) {
    if (value === null || typeof value !== 'object' || Array.isArray(value)) {
        throw new Error(`${name} must be an object.`);
    }
    return value;
}

function requireEqual(actual, expected, name) {
    if (actual !== expected) {
        throw new Error(`${name} must equal ${expected}.`);
    }
}

function requirePattern(value, pattern, name) {
    if (typeof value !== 'string' || !pattern.test(value)) {
        throw new Error(`${name} has an invalid format.`);
    }
}

function requireMember(value, allowed, name) {
    if (!allowed.includes(value)) {
        throw new Error(`${name} must be one of: ${allowed.join(', ')}.`);
    }
}

function requireRate(value, name) {
    if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > 1) {
        throw new Error(`${name} must be between 0 and 1.`);
    }
}

function requirePositiveNumber(value, name) {
    if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) {
        throw new Error(`${name} must be a positive number.`);
    }
}
