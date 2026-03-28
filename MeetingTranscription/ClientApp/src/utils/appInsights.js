import { APPINSIGHTS_CONNECTION_STRING } from '../config/apiBase';

const connectionString = APPINSIGHTS_CONNECTION_STRING;
let appInsights = null;

export async function initAppInsights() {
    if (!connectionString) {
        // No connection string configured; skip initialization in local/dev.
        // eslint-disable-next-line no-console
        console.warn('Application Insights connection string not provided. Skipping init.');
        return null;
    }

    if (appInsights) return appInsights;

    // Lazy-load the SDK so it doesn't land in the initial vendor bundle.
    try {
        const module = await import('@microsoft/applicationinsights-web');
        const { ApplicationInsights } = module;
        appInsights = new ApplicationInsights({
            config: {
                connectionString,
                enableAutoRouteTracking: true,
            },
        });
        appInsights.loadAppInsights();
        return appInsights;
    } catch (e) {
        // If the dynamic import fails, don't break the app.
        // eslint-disable-next-line no-console
        console.warn('Failed to load Application Insights SDK', e);
        return null;
    }
}

export function trackPageView(name) {
    if (!appInsights) return;
    try {
        appInsights.trackPageView({ name });
    } catch (e) {
        // ignore telemetry errors
    }
}
