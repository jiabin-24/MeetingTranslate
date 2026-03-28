// Base URL for API/websocket endpoints. Set via environment variable
// REACT_APP_API_BASE when building the app. Defaults to localhost for
// local development.
const runtimeEnv = (typeof window !== 'undefined' && window.__env) || {};

export const API_BASE = runtimeEnv.REACT_APP_API_BASE || process.env.REACT_APP_API_BASE || '';

export const USE_BYTE_DANCE = (runtimeEnv.REACT_APP_USE_BYTE_DANCE || process.env.REACT_APP_USE_BYTE_DANCE || 'true') === 'true';

export const APPINSIGHTS_CONNECTION_STRING = runtimeEnv.REACT_APP_APPINSIGHTS_CONNECTION_STRING
    || process.env.REACT_APP_APPINSIGHTS_CONNECTION_STRING || 'InstrumentationKey=ac6d0468-ba88-4aa2-99ab-b41ec0fc6cf7;IngestionEndpoint=https://eastasia-0.in.applicationinsights.azure.com/;LiveEndpoint=https://eastasia.livediagnostics.monitor.azure.com/;ApplicationId=874cda30-af64-4dd9-95c3-f78eb851d4a6';
