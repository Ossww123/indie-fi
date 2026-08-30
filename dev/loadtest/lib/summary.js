export function counterValue(data, metricName) {
    return data.metrics[metricName]?.values.count ?? 0;
}

export function trendSummary(data, metricName) {
    const values = data.metrics[metricName]?.values ?? {};
    return {
        count: values.count ?? 0,
        average: values.avg ?? null,
        p50: values.med ?? null,
        p95: values['p(95)'] ?? null,
        p99: values['p(99)'] ?? null,
        maximum: values.max ?? null,
    };
}

export function rateValue(data, metricName) {
    return data.metrics[metricName]?.values.rate ?? null;
}
