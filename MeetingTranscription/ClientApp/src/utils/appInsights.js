import { ApplicationInsights } from '@microsoft/applicationinsights-web';

const connectionString = process.env.REACT_APP_APPINSIGHTS_CONNECTION_STRING || '';
let appInsights = null;

export function initAppInsights() {
    if (!connectionString) {
        // No connection string configured; skip initialization in local/dev.
        // This allows builds to succeed without a secret in env.
        // Use `REACT_APP_APPINSIGHTS_CONNECTION_STRING` at build time to enable.
        // eslint-disable-next-line no-console
        console.warn('Application Insights connection string not provided. Skipping init.');
        return null;
    }

    if (appInsights) return appInsights;

    appInsights = new ApplicationInsights({
        config: {
            connectionString,
            enableAutoRouteTracking: true,
        },
    });
    appInsights.loadAppInsights();
    return appInsights;
}

export function trackPageView(name) {
    if (!appInsights) return;
    try {
        appInsights.trackPageView({ name });
    } catch (e) {
        // ignore telemetry errors
    }
}
