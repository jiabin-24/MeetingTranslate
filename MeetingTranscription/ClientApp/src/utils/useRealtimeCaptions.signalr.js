import { useEffect, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { API_BASE } from '../config/apiBase';

// This SignalR-based version mirrors useRealtimeCaptions.js so the two hooks are interchangeable.

// Maximum captions to keep in the client's in-memory list
const MAX_CAPTIONS = 100;

function mergeCaptions(prev, incoming) {
    const isFinal = !!incoming.isFinal;
    const hasWindow = incoming.startMs != null && incoming.endMs != null;
    if (isFinal && hasWindow) {
        // Simple de-duplication: remove any existing caption that has the same
        // startMs. This ensures the in-progress
        // partial (which may have no window) or prior final with slightly different
        // metadata won't leave a duplicate when the confirmed final arrives.
        const inStart = incoming.startMs;

        const filtered = prev.filter(l => {
            // If existing has same startMs, drop it (dedupe)
            return l.startMs !== inStart;
        });

        const next = sortByTime([...filtered, incoming]);
        if (next.length > MAX_CAPTIONS) return next.slice(next.length - MAX_CAPTIONS);
        return next;
    } else {
        // For partial (non-final) captions we should update a single existing line
        // instead of appending a new one each time. Match by speakerId (or speaker)
        // and sourceLang so interim updates replace the previous partial from
        // the same speaker/language pair.
        const matchKey = (c) => {
            const idA = c.speakerId || '';
            const idB = incoming.speakerId || '';
            const langA = c.sourceLang || '';
            const langB = incoming.sourceLang || '';
            return !c.isFinal && idA === idB && langA === langB;
        };

        const idx = prev.findIndex(matchKey);
        if (idx >= 0) {
            // Replace the existing partial entry in-place to keep list order stable
            const copy = prev.slice();
            copy[idx] = incoming;
            return copy;
        }

        // No existing partial for this speaker/lang — append to the end
        const next = sortByTime([...prev, incoming]);
        if (next.length > MAX_CAPTIONS) return next.slice(next.length - MAX_CAPTIONS);
        return next;
    }
}

function sortByTime(arr) {
    return arr.slice().sort((a, b) => {
        const sa = a.startMs ?? 0;
        const sb = b.startMs ?? 0;
        if (sa === sb) return (a.endMs ?? sa) - (b.endMs ?? sb);
        return sa - sb;
    });
}

export function useRealtimeCaptions(opts) {
    const [lines, setLines] = useState([]);
    const connRef = useRef(null);
    const reconnectRef = useRef(null);
    const backoffRef = useRef(1000);

    useEffect(() => {
        let abort = false;
        if (opts.meetingId) {
            (async () => {
                try {
                    const url = `${API_BASE}/api/meeting/getMeetingCaptions?threadId=${encodeURIComponent(opts.meetingId)}`;
                    const resp = await fetch(url, { method: 'GET', headers: { 'Accept': 'application/json' }, mode: 'cors', credentials: 'include' });
                    if (!resp.ok) return;
                    const data = await resp.json();
                    if (abort) return;
                    if (Array.isArray(data)) setLines(() => sortByTime(data));
                } catch (e) { }
            })();
        }

        let closed = false;

        const connect = () => {
            if (closed) return;
            const hubUrl = opts.hubUrl || opts.url;
            const conn = new signalR.HubConnectionBuilder()
                .withUrl(hubUrl)
                .withAutomaticReconnect({
                    nextRetryDelayInMilliseconds: ctx => 2000   // 每 2 秒重连一次，永不停止
                })
                .build();

            connRef.current = conn;

            const startAndAuth = async () => {
                await conn.start();
                backoffRef.current = 1000;
                try {
                    const token = (opts.currentUser && opts.currentUser.token) ? opts.currentUser.token : null;
                    try { await conn.invoke('Auth', token); } catch (e) { console.warn('Auth invoke failed', e); }
                    try { await conn.invoke('Subscribe', opts.meetingId, opts.targetLang); } catch (e) { console.warn('Subscribe invoke failed', e); }
                } catch (e) { console.warn('SignalR start/auth failed', e); }
            };

            conn.on('caption', (msg) => {
                try { setLines(prev => mergeCaptions(prev, msg)); } catch (_) { }
            });

            conn.onclose(() => {
                if (closed) return;
                if (reconnectRef.current != null) return;
                reconnectRef.current = window.setTimeout(() => {
                    reconnectRef.current = null;
                    backoffRef.current = Math.min(backoffRef.current * 2, 15000);
                    connect();
                }, backoffRef.current);
            });

            startAndAuth().catch(err => console.warn('SignalR start failed', err));
        };

        connect();

        return () => {
            closed = true;
            abort = true;
            try {
                const c = connRef.current;
                if (c) {
                    if (typeof c.stop === 'function') c.stop().catch(() => { });
                }
            } catch (_) { }
            if (reconnectRef.current) window.clearTimeout(reconnectRef.current);
        };
    }, [opts.url, opts.hubUrl, opts.meetingId]);

    // When targetLang changes, ask the server to update this connection's subscription
    useEffect(() => {
        const conn = connRef.current;
        if (!conn) return;
        // Try to re-subscribe on the existing connection. If the connection isn't ready
        // the invoke will fail when the connection isn't 'Connected'. Guard
        // against that to avoid noisy console errors.
        (async () => {
            try {
                if (opts.meetingId) {
                    if (conn.state === signalR.HubConnectionState.Connected) {
                        try { await conn.invoke('Subscribe', opts.meetingId, opts.targetLang); } catch (e) { console.warn('Resubscribe invoke failed', e); }
                    }
                }
            } catch (e) { /* ignore */ }
        })();
    }, [opts.targetLang, opts.meetingId]);

    return { lines };
}
