import React from 'react';
import ReactDOM from 'react-dom/client';
import './index.css';
import { AppRoute } from './router/router';
import { initAppInsights, trackPageView } from './utils/appInsights';

// Initialize Application Insights (reads from REACT_APP_APPINSIGHTS_CONNECTION_STRING)
// The SDK is lazy-loaded inside initAppInsights to avoid adding it to the initial bundle.
initAppInsights().then(() => {
    try {
        trackPageView(window.location.pathname || '/');
    } catch { }
}).catch(() => { /* ignore init errors */ });

const root = ReactDOM.createRoot(document.getElementById('root'));
root.render(
    <React.StrictMode>
        <AppRoute />
    </React.StrictMode>
);
