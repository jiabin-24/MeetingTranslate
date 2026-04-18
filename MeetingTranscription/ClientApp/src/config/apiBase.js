// Base URL for API/websocket endpoints. Set via environment variable
// REACT_APP_API_BASE when building the app. Defaults to localhost for
// local development.
const runtimeEnv = (typeof window !== 'undefined' && window.__env) || {};

export const API_BASE = runtimeEnv.REACT_APP_API_BASE || process.env.REACT_APP_API_BASE || '';

export const USE_BYTE_DANCE = (runtimeEnv.REACT_APP_USE_BYTE_DANCE || process.env.REACT_APP_USE_BYTE_DANCE || 'true') === 'true';

export const APPINSIGHTS_CONNECTION_STRING = runtimeEnv.REACT_APP_APPINSIGHTS_CONNECTION_STRING
    || process.env.REACT_APP_APPINSIGHTS_CONNECTION_STRING || 'InstrumentationKey=417b3b95-89c4-4cce-99d0-4d6ea191f711;IngestionEndpoint=https://eastasia-0.in.applicationinsights.azure.com/;LiveEndpoint=https://eastasia.livediagnostics.monitor.azure.com/;ApplicationId=fd32a292-b97b-487d-b94e-dede5b003c4e';
