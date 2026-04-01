import { useEffect, useRef, useState } from 'react';
import { API_BASE } from '../config/apiBase';

// This file implements a manager that shares a single SignalR connection and
// initial fetch per unique (meetingId, targetLang, hubUrl) key. Components
// subscribe to the manager to receive `lines`. When the last subscriber
// unsubscribes the connection is stopped.

const MAX_CAPTIONS = 100;

function mergeCaptions(prev, incoming) {
    const isFinal = !!incoming.isFinal;
    const hasWindow = incoming.startMs != null && incoming.endMs != null;
    if (isFinal && hasWindow) {
        let next = sortByTime([...prev, incoming]);
        next = next.filter(l => {
            if (incoming.speakerId) {
                return l.speakerId !== incoming.speakerId || l.isFinal === true;
            }
            return l.startMs !== incoming.startMs;
        });
        if (next.length > MAX_CAPTIONS) return next.slice(next.length - MAX_CAPTIONS);
        return next;
    } else {
        const matchKey = (c) => {
            const idA = c.speakerId || '';
            const idB = incoming.speakerId || '';
            const langA = c.sourceLang || '';
            const langB = incoming.sourceLang || '';
            return !c.isFinal && idA === idB && langA === langB;
        };

        const idx = prev.findIndex(matchKey);
        if (idx >= 0) {
            const copy = prev.slice();
            copy[idx] = incoming;
            return copy;
        }

        const next = sortByTime([...prev, incoming]);
        if (next.length > MAX_CAPTIONS) return next.slice(next.length - MAX_CAPTIONS);
        return next;
    }
}

function sortByTime(arr) {
    return arr.slice().sort((a, b) => {
        const aFinal = !!a.isFinal;
        const bFinal = !!b.isFinal;
        if (aFinal !== bFinal) {
            const aSpeaker = a.speakerId ?? '';
            const bSpeaker = b.speakerId ?? '';
            if (aSpeaker === bSpeaker) return aFinal ? -1 : 1;
        }

        const sa = a.startMs ?? 0;
        const sb = b.startMs ?? 0;
        if (sa === sb) return (a.endMs ?? sa) - (b.endMs ?? sb);
        return sa - sb;
    });
}

const managers = new Map();

function managerKey(meetingId, targetLang, hubUrl) {
    return `${meetingId || '__nomid__'}|${targetLang || '__notg__'}|${hubUrl || '__nourl__'}`;
}

function createManager({ meetingId, targetLang, hubUrl, currentUser }) {
    let lines = [];
    const subs = new Set();
    let conn = null;
    let reconnectRef = null;
    let backoff = 1000;
    let closed = false;
    let started = false; // make start() idempotent
    let shutdownRef = null; // debounce stopping when last subscriber leaves

    const notifyAll = () => subs.forEach(cb => {
        try { cb(lines); } catch (_) { }
    });

    const doInitialFetch = async () => {
        if (!meetingId) return;
        try {
            const url = `${API_BASE}/api/meeting/getMeetingCaptions?threadId=${encodeURIComponent(meetingId)}`;
            const resp = await fetch(url, { method: 'GET', headers: { 'Accept': 'application/json' }, mode: 'cors', credentials: 'include' });
            if (!resp.ok) return;
            const data = await resp.json();
            if (Array.isArray(data)) {
                lines = sortByTime(data);
                notifyAll();
            }
        } catch (_) { }
    };

    const connect = () => {
        if (closed) return;
        (async () => {
            try {
                const signalR = await import('@microsoft/signalr');
                const hub = hubUrl || '';
                conn = new signalR.HubConnectionBuilder()
                    .withUrl(hub)
                    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: () => 2000 })
                    .build();

                conn.on('caption', (msg) => {
                    try { lines = mergeCaptions(lines, msg); notifyAll(); } catch (_) { }
                });

                conn.onclose(() => {
                    if (closed) return;
                    if (reconnectRef != null) return;
                    reconnectRef = window.setTimeout(() => {
                        reconnectRef = null;
                        backoff = Math.min(backoff * 2, 15000);
                        connect();
                    }, backoff);
                });

                await conn.start();
                backoff = 1000;
                const token = (currentUser && currentUser.token) ? currentUser.token : null;
                try { await conn.invoke('Auth', token); } catch (e) { console.warn('Auth invoke failed', e); }
                try { await conn.invoke('Subscribe', meetingId, targetLang); } catch (e) { console.warn('Subscribe invoke failed', e); }
            } catch (e) {
                console.warn('SignalR start/auth failed', e);
            }
        })();
    };

    const stop = async () => {
        closed = true;
        try {
            if (reconnectRef) window.clearTimeout(reconnectRef);
        } catch (_) { }
        try {
            if (conn && typeof conn.stop === 'function') await conn.stop();
        } catch (_) { }
        conn = null;
        reconnectRef = null;
    };

    const start = async () => {
        if (started) return;
        started = true;
        await doInitialFetch();
        if (closed) return;
        connect();
    };

    return {
        start,
        subscribe(cb) {
            // If a shutdown was scheduled because all subscribers had left,
            // cancel it — a new subscriber is coming back shortly.
            if (shutdownRef) {
                try { window.clearTimeout(shutdownRef); } catch (_) { }
                shutdownRef = null;
            }

            subs.add(cb);
            // send current snapshot immediately
            try { cb(lines); } catch (_) { }
            return () => {
                subs.delete(cb);
                if (subs.size === 0) {
                    // Debounce the stop so transient unmount/remount (React StrictMode)
                    // doesn't tear down the manager and cause a duplicate fetch.
                    try { shutdownRef = window.setTimeout(() => {
                        shutdownRef = null;
                        stop().catch(() => { });
                        managers.delete(managerKey(meetingId, targetLang, hubUrl));
                    }, 1000); } catch (_) { /* ignore */ }
                }
            };
        }
    };
}

export function useRealtimeCaptions(opts) {
    
    const [lines, setLines] = useState([]);
    const subRef = useRef(null);

    useEffect(() => {
        const key = managerKey(opts.meetingId, opts.targetLang, opts.hubUrl || opts.url);
        let mgr = managers.get(key);
        if (!mgr) {
            mgr = createManager({ meetingId: opts.meetingId, targetLang: opts.targetLang, hubUrl: opts.hubUrl || opts.url, currentUser: opts.currentUser });
            managers.set(key, mgr);
            mgr.start().catch(() => { managers.delete(key); });
        }

        subRef.current = mgr.subscribe(newLines => setLines(newLines));

        return () => {
            try { if (subRef.current) subRef.current(); } catch (_) { }
            subRef.current = null;
        };
    }, [opts.meetingId, opts.targetLang, opts.url, opts.hubUrl, opts.currentUser && opts.currentUser.token]);

    return { lines };
}
