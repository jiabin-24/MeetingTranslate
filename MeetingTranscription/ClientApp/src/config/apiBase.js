// Base URL for API/websocket endpoints. Set via environment variable
// REACT_APP_API_BASE when building the app. Defaults to localhost for
// local development.
export const API_BASE = process.env.REACT_APP_API_BASE || '';

export const USE_BYTE_DANCE = process.env.REACT_APP_USE_BYTE_DANCE === 'true';