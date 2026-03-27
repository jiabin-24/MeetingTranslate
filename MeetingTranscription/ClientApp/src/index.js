import React from 'react';
import ReactDOM from 'react-dom/client';
import './index.css';
import { AppRoute } from './router/router';
import { initAppInsights, trackPageView } from './utils/appInsights';

// Initialize Application Insights (reads from REACT_APP_APPINSIGHTS_CONNECTION_STRING)
const ai = initAppInsights();
if (ai) {
    trackPageView(window.location.pathname || '/');
}

const root = ReactDOM.createRoot(document.getElementById('root'));
root.render(
    <React.StrictMode>
        <AppRoute />
    </React.StrictMode>
);
