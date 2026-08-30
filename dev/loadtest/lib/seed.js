export function loadSeedManifest(contract) {
    const path = __ENV.SEED_MANIFEST_FILE;
    if (typeof path !== 'string' || path.trim() === '') {
        throw new Error('SEED_MANIFEST_FILE is required.');
    }

    let manifest;
    try {
        manifest = JSON.parse(open(import.meta.resolve(path)));
    } catch (error) {
        throw new Error(`Could not read seed manifest: ${error.message}`);
    }

    equal(manifest.schemaVersion, 1, 'seed.schemaVersion');
    equal(manifest.runId, contract.runId, 'seed.runId');
    equal(manifest.commit, contract.commit, 'seed.commit');
    equal(trimOrigin(manifest.baseUrl), contract.baseUrl, 'seed.baseUrl');
    if (manifest.marker !== `operational-validation:${contract.runId}`) {
        throw new Error('seed.marker does not match the run id.');
    }

    object(manifest.accounts, 'seed.accounts');
    const pools = ['owners', 'claimers', 'latecomers', 'retries'];
    let accountTotal = 0;
    for (const name of pools) {
        validatePool(manifest.accounts[name], `seed.accounts.${name}`);
        accountTotal += manifest.accounts[name].count;
    }
    equal(manifest.accounts.total, accountTotal, 'seed.accounts.total');
    positiveInteger(manifest.catalog?.gameCount, 'seed.catalog.gameCount');
    if (!Array.isArray(manifest.catalog.gameIds)
        || manifest.catalog.gameIds.length !== manifest.catalog.gameCount
        || new Set(manifest.catalog.gameIds).size !== manifest.catalog.gameCount
        || manifest.catalog.gameIds.some((id) => !Number.isInteger(id) || id <= 0)) {
        throw new Error('seed.catalog.gameIds must contain each seeded game id exactly once.');
    }
    positiveInteger(manifest.drop?.id, 'seed.drop.id');
    positiveInteger(manifest.drop?.gameId, 'seed.drop.gameId');
    positiveInteger(manifest.drop?.keyCount, 'seed.drop.keyCount');
    if (Number.isNaN(Date.parse(manifest.drop.opensAtUtc))) {
        throw new Error('seed.drop.opensAtUtc must be an ISO-8601 timestamp.');
    }

    return Object.freeze(manifest);
}

export function poolAccountEmail(manifest, poolName, number) {
    const pool = manifest.accounts[poolName];
    validatePool(pool, `seed.accounts.${poolName}`);
    if (!Number.isInteger(number) || number <= 0 || number > pool.count) {
        throw new Error(`Account number is outside the ${poolName} pool.`);
    }
    return `${pool.emailPrefix}${String(number).padStart(5, '0')}@example.test`;
}

export function publicSeedMetadata(manifest) {
    return {
        schemaVersion: manifest.schemaVersion,
        runId: manifest.runId,
        createdAtUtc: manifest.createdAtUtc,
        commit: manifest.commit,
        accounts: {
            total: manifest.accounts.total,
            owners: manifest.accounts.owners.count,
            claimers: manifest.accounts.claimers.count,
            latecomers: manifest.accounts.latecomers.count,
            retries: manifest.accounts.retries.count,
        },
        catalog: {
            gameCount: manifest.catalog.gameCount,
            firstGameId: manifest.catalog.firstGameId,
            lastGameId: manifest.catalog.lastGameId,
        },
        drop: manifest.drop,
    };
}

function validatePool(pool, name) {
    object(pool, name);
    positiveOrZeroInteger(pool.count, `${name}.count`);
    if (typeof pool.emailPrefix !== 'string'
        || !/^opv-[a-f0-9]{12}-(owner|claimer|late|retry)-$/.test(pool.emailPrefix)) {
        throw new Error(`${name}.emailPrefix has an invalid format.`);
    }
}

function trimOrigin(value) {
    return typeof value === 'string' ? value.replace(/\/+$/, '') : value;
}

function object(value, name) {
    if (value === null || typeof value !== 'object' || Array.isArray(value)) {
        throw new Error(`${name} must be an object.`);
    }
}

function equal(actual, expected, name) {
    if (actual !== expected) {
        throw new Error(`${name} does not match the execution contract.`);
    }
}

function positiveInteger(value, name) {
    if (!Number.isInteger(value) || value <= 0) {
        throw new Error(`${name} must be a positive integer.`);
    }
}

function positiveOrZeroInteger(value, name) {
    if (!Number.isInteger(value) || value < 0) {
        throw new Error(`${name} must be a non-negative integer.`);
    }
}
