import { useEffect, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { API_BASE } from '../config/apiBase';

// This SignalR-based version mirrors useRealtimeCaptions.js so the two hooks are interchangeable.

// Module-level queue and unlock state to handle browser autoplay restrictions.
const _pendingAudioBlobs = [];
// Maximum captions to keep in the client's in-memory list
const MAX_CAPTIONS = 100;
let _audioUnlocked = false;
let _interactionHandlersInstalled = false;
let _suppressPlayback = false;
let _audioCtx = null;
let _currentAudioEl = null;
let _currentAudioUrl = null;
let _urlInUse = false;
let _currentAudioSource = null;
const _activeAudioEls = new Set();
const _activeHookInstances = new Set();
const _playbackQueue = [];
let _processingQueue = false;

// Stop current HTMLAudioElement or WebAudio source but do NOT clear the pending queue.
function stopCurrentPlayback() {
    try {
        // Stop any tracked HTMLAudioElements aggressively
        try {
            for (const a of Array.from(_activeAudioEls)) {
                try { a.pause(); } catch (_) { }
                try { a.muted = true; } catch (_) { }
                try { a.volume = 0; } catch (_) { }
                try { a.currentTime = 0; } catch (_) { }
                try { a.srcObject = null; } catch (_) { }
                try { a.src = ''; } catch (_) { }
                try { a.load(); } catch (_) { }
                try { _activeAudioEls.delete(a); } catch (_) { }
            }

            
            _currentAudioEl = null;
        } catch (_) { }
        // Also try to stop any <audio> elements in the document in case some were created elsewhere
        try {
            const els = Array.from(document.getElementsByTagName('audio'));
            for (const a of els) {
                try { a.pause(); } catch (_) { }
                try { a.muted = true; } catch (_) { }
                try { a.volume = 0; } catch (_) { }
                try { a.currentTime = 0; } catch (_) { }
                try { a.srcObject = null; } catch (_) { }
                try { a.src = ''; } catch (_) { }
                try { a.load(); } catch (_) { }
            }
        } catch (_) { }
        if (_currentAudioUrl) {
            try { URL.revokeObjectURL(_currentAudioUrl); } catch (_) { }
            _currentAudioUrl = null;
        }
        // cancel speech synthesis if any
        try { if (window.speechSynthesis && typeof window.speechSynthesis.cancel === 'function') window.speechSynthesis.cancel(); } catch (_) { }
        if (_currentAudioSource) {
            try { _currentAudioSource.onended = null; } catch (_) { }
            try { _currentAudioSource.stop(); } catch (_) { }
            try { _currentAudioSource.disconnect(); } catch (_) { }
            _currentAudioSource = null;
        }
    } catch (e) {
        console.warn('stopCurrentPlayback failed', e);
    }
}

function isPlaying() {
    try {
        if (_urlInUse) return true;
        if (_currentAudioSource) return true;
        if (_activeAudioEls.size > 0) return true;
    } catch (_) { }
    return false;
}

function enqueuePlayback(item) {
    // item: { blob, url, chunks }
    return new Promise((resolve, reject) => {
        try {
            _playbackQueue.push({ item, resolve, reject });
            try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'queued' } })); } catch (_) { }
            // kick off processing if not already
            if (!_processingQueue) processPlaybackQueue();
        } catch (e) {
            console.warn('enqueuePlayback failed', e);
            reject(e);
        }
    });
}

async function processPlaybackQueue() {
    if (_processingQueue) return;
    _processingQueue = true;
    try {
        while (_playbackQueue.length && !_suppressPlayback) {
            const entry = _playbackQueue.shift();
            const { item, resolve, reject } = entry;
            try {
                // try HTMLAudio first
                await (async () => {
                    const { url, blob, chunks } = item || {};
                    // attempt element playback
                    await new Promise((res, rej) => {
                        try {
                            const audio = new Audio(url);
                            _activeAudioEls.add(audio);
                            _currentAudioEl = audio;
                            _currentAudioUrl = url;
                            _urlInUse = true;
                            audio.autoplay = true;
                            const cleanup = () => {
                                try { audio.removeEventListener('ended', onEnd); } catch (_) { }
                                try { audio.removeEventListener('error', onError); } catch (_) { }
                                try { _activeAudioEls.delete(audio); } catch (_) { }
                                if (_currentAudioEl === audio) _currentAudioEl = null;
                                if (_currentAudioUrl === url) { try { if (!_urlInUse) URL.revokeObjectURL(_currentAudioUrl); } catch (_) { }; _currentAudioUrl = null; }
                            };
                            const onEnd = () => { _urlInUse = false; try { URL.revokeObjectURL(url); } catch (_) { }; cleanup(); res(); };
                            const onError = (e) => { _urlInUse = false; try { URL.revokeObjectURL(url); } catch (_) { }; cleanup(); rej(e || new Error('Audio playback error')); };
                            audio.addEventListener('ended', onEnd);
                            audio.addEventListener('error', onError);
                            const p = audio.play();
                            if (p && typeof p.catch === 'function') {
                                p.catch(err => {
                                    cleanup();
                                    rej(err);
                                });
                            } 
                        } catch (e) {
                            rej(e);
                        }
                    });
                })();
                try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'idle' } })); } catch (_) { }
                resolve();
            } catch (e) {
                // If autoplay blocked, push to pending and stop processing until unlock
                if (isNotAllowedError(e)) {
                    try { _pendingAudioBlobs.push({ blob: item && item.blob ? item.blob : null, url: item && item.url ? item.url : null }); } catch (_) { }
                    try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'queued' } })); } catch (_) { }
                    setupInteractionUnlock();
                    // reject this entry so caller knows it didn't complete
                    try { entry.reject && entry.reject(e); } catch (_) { }
                    break; // stop processing until unlock
                }

                // If a stop was requested after we started playback, avoid attempting any fallback
                if (_suppressPlayback) {
                    try { entry.reject && entry.reject(new Error('Playback suppressed')); } catch (_) { }
                    break;
                }

                // If NotSupported or decode error, try WebAudio fallback for this item
                try {
                    try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'playing' } })); } catch (_) { }
                    // If chunks are provided (assembled frames), use them; otherwise try to decode blob or fetch the url
                    if (item && item.chunks && item.chunks.length) {
                        // respect stop request before heavy decode
                        if (_suppressPlayback) { try { entry.reject && entry.reject(new Error('Playback suppressed')); } catch (_) {} ; break; }
                        await playChunksWithWebAudio(item.chunks);
                    } else if (item && item.blob) {
                        try {
                            if (_suppressPlayback) throw new Error('Playback suppressed');
                            const ab = await item.blob.arrayBuffer();
                            if (_suppressPlayback) throw new Error('Playback suppressed');
                            await playChunksWithWebAudio([ab]);
                        } catch (blobErr) {
                            console.warn('Blob->WebAudio decode failed', blobErr);
                            throw blobErr;
                        }
                    } else if (item && item.url) {
                        try {
                            if (_suppressPlayback) throw new Error('Playback suppressed');
                            const resp = await fetch(item.url);
                            const ab = await resp.arrayBuffer();
                            if (_suppressPlayback) throw new Error('Playback suppressed');
                            await playChunksWithWebAudio([ab]);
                        } catch (urlErr) {
                            console.warn('URL->WebAudio fetch/decode failed', urlErr);
                            throw urlErr;
                        }
                    } else {
                        // nothing to decode
                        throw new Error('No audio data available for WebAudio fallback');
                    }
                    try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'idle' } })); } catch (_) { }
                    resolve();
                } catch (we) {
                    console.warn('Playback of queued item failed', we);
                    try { entry.reject && entry.reject(we); } catch (_) { }
                }
            }
        }
    } catch (e) {
        console.warn('processPlaybackQueue failed', e);
    } finally {
        _processingQueue = false;
    }
}

function ensureAudioContext() {
    if (!_audioCtx) {
        const Ctx = window.AudioContext || window.webkitAudioContext;
        if (!Ctx) return null;
        _audioCtx = new Ctx();
    }

    return _audioCtx;
}

// Helper to wrap raw PCM16LE buffers into RIFF WAV (little endian)
function makeWavFromPcm16LE(buffer, sampleRate = 16000, channels = 1) {
    const samples = new Uint8Array(buffer);
    const byteRate = sampleRate * channels * 2;
    const blockAlign = channels * 2;
    const wavBuffer = new ArrayBuffer(44 + samples.length);
    const view = new DataView(wavBuffer);

    function writeString(view, offset, string) {
        for (let i = 0; i < string.length; i++) {
            view.setUint8(offset + i, string.charCodeAt(i));
        }
    }

    /* RIFF identifier */ writeString(view, 0, 'RIFF');
    /* file length */ view.setUint32(4, 36 + samples.length, true);
    /* RIFF type */ writeString(view, 8, 'WAVE');
    /* format chunk identifier */ writeString(view, 12, 'fmt ');
    /* format chunk length */ view.setUint32(16, 16, true);
    /* sample format (raw) */ view.setUint16(20, 1, true);
    /* channel count */ view.setUint16(22, channels, true);
    /* sample rate */ view.setUint32(24, sampleRate, true);
    /* byte rate (sampleRate * blockAlign) */ view.setUint32(28, byteRate, true);
    /* block align (channel count * bytes per sample) */ view.setUint16(32, blockAlign, true);
    /* bits per sample */ view.setUint16(34, 16, true);
    /* data chunk identifier */ writeString(view, 36, 'data');
    /* data chunk length */ view.setUint32(40, samples.length, true);

    // write the PCM samples
    const bytes = new Uint8Array(wavBuffer, 44);
    bytes.set(samples);
    return wavBuffer;
}

function isNotAllowedError(err) {
    if (!err) return false;
    const name = err.name || '';
    const msg = String(err.message || '');
    return name === 'NotAllowedError' || /didn't interact/.test(msg) || /not allowed to play/.test(msg);
}

function setupInteractionUnlock() {
    if (_audioUnlocked) return;
    if (_interactionHandlersInstalled) return;
    _interactionHandlersInstalled = true;
    const handler = async () => {
        try {
            await unlockPendingAudio();
        } finally {
            window.removeEventListener('click', handler);
            window.removeEventListener('keydown', handler);
            window.removeEventListener('touchstart', handler);
            _interactionHandlersInstalled = false;
        }
    };
    window.addEventListener('click', handler, { once: true });
    window.addEventListener('keydown', handler, { once: true });
    window.addEventListener('touchstart', handler, { once: true });
}

async function playChunksWithWebAudio(chunks) {
    const ctx = ensureAudioContext();
    if (!ctx) throw new Error('WebAudio not supported');
    let totalLen = 0;
    for (const c of chunks) totalLen += (c.byteLength || 0);
    const merged = new Uint8Array(totalLen);
    let offset = 0;
    for (const c of chunks) { const u8 = new Uint8Array(c); merged.set(u8, offset); offset += u8.length; }
    try {
        const audioBuffer = await ctx.decodeAudioData(merged.buffer.slice(0));
        const src = ctx.createBufferSource();
        _currentAudioSource = src;
        src.buffer = audioBuffer;
        src.connect(ctx.destination);
        return new Promise((resolve) => {
            src.onended = () => { try { src.disconnect(); } catch { }; _currentAudioSource = null; resolve(); };
            src.start(0);
        });
    } catch (e) {
        try {
            const header = Array.from(new Uint8Array(merged.buffer.slice(0, 12))).map(b => b.toString(16).padStart(2, '0')).join(' ');
            console.warn('WebAudio decode failed; first bytes:', header);
        } catch (_) { }
        throw new Error('Unable to decode audio data');
    }
}

async function unlockPendingAudio() {
    // Mark unlocked so subsequent plays are allowed
    _audioUnlocked = true;
    // clear suppression when user explicitly unlocks playback
    _suppressPlayback = false;

    // Resume WebAudio / SpeechSynthesis where applicable
    try {
        if (window.speechSynthesis && typeof window.speechSynthesis.resume === 'function') {
            window.speechSynthesis.resume();
        }
        const ctx = ensureAudioContext();
        if (ctx && typeof ctx.resume === 'function') {
            ctx.resume().catch(() => { });
        }
    } catch { }

    // Play queued blobs sequentially. Each entry should be { blob, url }.
    while (_pendingAudioBlobs.length) {
        const entry = _pendingAudioBlobs.shift();
        const blob = entry && entry.blob ? entry.blob : entry;
        let url = entry && entry.url ? entry.url : null;
        if (!url) url = URL.createObjectURL(blob);
        try {
            // enqueue playback instead of immediate play
            try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'queued' } })); } catch (_) { }
            enqueuePlayback({ blob, url, chunks: null });
        } catch (e) {
            // If it still fails, just drop it
            console.warn('Playback of queued audio failed', e);
        } finally {
            try {
                if (_urlInUse) {
                    // defer revoke slightly if playback still using it
                    setTimeout(() => { try { if (!_urlInUse) URL.revokeObjectURL(url); } catch (_) { } }, 200);
                } else {
                    try { URL.revokeObjectURL(url); } catch (_) { }
                }
            } catch (_) { }
        }
    }
}

function stopAudio() {
    try {
        _suppressPlayback = true;
        stopCurrentPlayback();
        // Clear the playback queue: revoke any object URLs and reject queued promises
        try {
            while (_playbackQueue.length) {
                const q = _playbackQueue.shift();
                try {
                    if (q && q.item && q.item.url) {
                        try { URL.revokeObjectURL(q.item.url); } catch (_) {}
                    }
                } catch (_) {}
                try { if (q && typeof q.reject === 'function') q.reject(new Error('Playback stopped')); } catch (_) {}
            }
        } catch (_) {}
        try {
            for (const entry of _pendingAudioBlobs) {
                if (entry && entry.url) {
                    try { URL.revokeObjectURL(entry.url); } catch (_) { }
                }
            }
        } catch (_) { }
        _pendingAudioBlobs.length = 0;
        try {
            for (const inst of Array.from(_activeHookInstances)) {
                try { if (inst.audioBuffersRef && inst.audioBuffersRef.current) inst.audioBuffersRef.current.clear(); } catch (_) { }
                try { if (inst.audioQueueRef && inst.audioQueueRef.current) inst.audioQueueRef.current.length = 0; } catch (_) { }
            }
        } catch (_) { }
        try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'idle' } })); } catch (_) { }
    } catch (e) {
        console.warn('stopAudio failed', e);
    }
}

function mergeCaptions(prev, incoming) {
    const isFinal = !!incoming.isFinal;
    const hasWindow = incoming.startMs != null && incoming.endMs != null;
    if (isFinal && hasWindow) {
        // Simple de-duplication: remove any existing caption that has the same
        // speakerId (or speaker) AND the same startMs. This ensures the in-progress
        // partial (which may have no window) or prior final with slightly different
        // metadata won't leave a duplicate when the confirmed final arrives.
        const inSpeaker = incoming.speakerId || '';
        const inStart = incoming.startMs;

        const filtered = prev.filter(l => {
            const existingSpeaker = l.speakerId || '';
            if (!existingSpeaker) return true;
            if (existingSpeaker !== inSpeaker) return true;
            // If existing has same startMs, drop it (dedupe)
            if (l.startMs === inStart) return false;
            return true;
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
            const idB = incoming.speakerId ||  '';
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
        // Put final captions before partial (non-final) ones
        const aFinal = !!a.isFinal;
        const bFinal = !!b.isFinal;
        if (aFinal !== bFinal) return aFinal ? -1 : 1;

        const sa = a.startMs ?? 0;
        const sb = b.startMs ?? 0;
        if (sa === sb) return (a.endMs ?? sa) - (b.endMs ?? sb);
        return sa - sb;
    });
}

export function useRealtimeCaptions(opts) {
    const [lines, setLines] = useState([]);
    const connRef = useRef(null);
    const audioBuffersRef = useRef(new Map());
    const audioQueueRef = useRef([]);
    const _hookInstance = { audioBuffersRef, audioQueueRef };
    try { _activeHookInstances.add(_hookInstance); } catch (_) { }
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
                .withAutomaticReconnect()
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

            conn.on('audio', async (meta, audio) => {
                try {
                    if (!meta) return;
                    // If the audio metadata indicates the speaker is the current user,
                    // skip playback (do not store metadata or play the binary frames).
                    try {
                        const currentUserId = opts.currentUser && opts.currentUser.id;
                        if (currentUserId && meta && meta.speakerId && String(meta.speakerId) === String(currentUserId)) {
                            return;
                        }
                    } catch (_) { }

                    // If user hasn't enabled audio playback yet, drop both meta and binary
                    if (!_audioUnlocked) {
                        return;
                    }

                    // If meta-only (server might send meta then send binary separately), store metadata
                    if (audio === undefined || audio === null) {
                        const audioKey = meta.audioId || meta.meetingId || (`audio:${Date.now()}`);
                        audioBuffersRef.current.set(audioKey, { contentType: meta.contentType || '', chunks: [], isFinal: !!meta.isFinal, headerHex: meta.headerHex });
                        audioQueueRef.current.push(audioKey);
                        return;
                    }

                    // Convert audio payload to ArrayBuffer
                    const toArrayBuffer = async (payload) => {
                        if (payload instanceof ArrayBuffer) return payload;
                        if (payload && payload.data instanceof ArrayBuffer) return payload.data;
                        if (payload && typeof payload.arrayBuffer === 'function') return await payload.arrayBuffer();
                        if (typeof payload === 'string') {
                            const binary = atob(payload);
                            const len = binary.length;
                            const u8 = new Uint8Array(len);
                            for (let i = 0; i < len; i++) u8[i] = binary.charCodeAt(i);
                            return u8.buffer;
                        }
                        return null;
                    };

                    const ab = await toArrayBuffer(audio);
                    if (!ab) return;

                    // Determine associated metadata key
                    const audioKey = meta.audioId || meta.meetingId || (audioQueueRef.current.length ? audioQueueRef.current.shift() : null) || null;

                    if (audioKey && audioBuffersRef.current.has(audioKey)) {
                        const entry = audioBuffersRef.current.get(audioKey);
                        entry.chunks.push(ab);
                        if (entry.isFinal) {
                            // assemble
                            let totalLen = 0;
                            for (const c of entry.chunks) totalLen += (c.byteLength || 0);
                            const merged = new Uint8Array(totalLen);
                            let o = 0;
                            for (const c of entry.chunks) { const u = new Uint8Array(c); merged.set(u, o); o += u.length; }

                            // detect container headers
                            const findSeq = (arr, seq) => {
                                for (let i = 0; i + seq.length <= arr.length; i++) {
                                    let ok = true;
                                    for (let j = 0; j < seq.length; j++) if (arr[i + j] !== seq[j]) { ok = false; break; }
                                    if (ok) return i;
                                }
                                return -1;
                            };

                            const riffIdx = findSeq(merged, [0x52, 0x49, 0x46, 0x46]);
                            const id3Idx = findSeq(merged, [0x49, 0x44, 0x33]);
                            let mp3Idx = -1;
                            for (let i = 0; i + 1 < merged.length; i++) {
                                if (merged[i] === 0xFF && (merged[i + 1] & 0xE0) === 0xE0) { mp3Idx = i; break; }
                            }

                            let blob = null;
                            if (riffIdx >= 0) {
                                const slice = merged.slice(riffIdx);
                                blob = new Blob([slice], { type: entry.contentType || 'audio/wav' });
                            } else if (id3Idx >= 0 || mp3Idx >= 0 || (entry.contentType && entry.contentType.includes('mpeg'))) {
                                const idx = id3Idx >= 0 ? id3Idx : (mp3Idx >= 0 ? mp3Idx : 0);
                                const slice = merged.slice(idx);
                                blob = new Blob([slice], { type: entry.contentType || 'audio/mpeg' });
                            } else {
                                const wavBuf = makeWavFromPcm16LE(merged.buffer, 16000, 1);
                                blob = new Blob([wavBuf], { type: 'audio/wav' });
                            }

                            try {
                                const url = URL.createObjectURL(blob);
                                enqueuePlayback({ blob, url, chunks: entry.chunks.slice() }).catch(() => {});
                            } catch (e) {
                                console.warn('Failed to enqueue assembled audio', e);
                            }

                            audioBuffersRef.current.delete(audioKey);
                        }
                    } else {
                        // no metadata �� treat single-chunk
                        const merged = new Uint8Array(ab);
                        const findSeq = (arr, seq) => {
                            for (let i = 0; i + seq.length <= arr.length; i++) {
                                let ok = true;
                                for (let j = 0; j < seq.length; j++) if (arr[i + j] !== seq[j]) { ok = false; break; }
                                if (ok) return i;
                            }
                            return -1;
                        };

                        const riffIdx = findSeq(merged, [0x52, 0x49, 0x46, 0x46]);
                        const id3Idx = findSeq(merged, [0x49, 0x44, 0x33]);
                        let mp3Idx = -1;
                        for (let i = 0; i + 1 < merged.length; i++) {
                            if (merged[i] === 0xFF && (merged[i + 1] & 0xE0) === 0xE0) { mp3Idx = i; break; }
                        }

                        let blob = null;
                        if (riffIdx >= 0) {
                            const slice = merged.slice(riffIdx);
                            blob = new Blob([slice], { type: meta.contentType || 'audio/wav' });
                        } else if (id3Idx >= 0 || mp3Idx >= 0 || (meta.contentType && meta.contentType.includes('mpeg'))) {
                            const idx = id3Idx >= 0 ? id3Idx : (mp3Idx >= 0 ? mp3Idx : 0);
                            const slice = merged.slice(idx);
                            blob = new Blob([slice], { type: meta.contentType || 'audio/mpeg' });
                        } else {
                            const wavBuf = makeWavFromPcm16LE(merged.buffer, 16000, 1);
                            blob = new Blob([wavBuf], { type: 'audio/wav' });
                        }

                        try {
                            const url = URL.createObjectURL(blob);
                            enqueuePlayback({ blob, url, chunks: [ab] }).catch(() => {});
                        } catch (e) {
                            console.warn('Failed to enqueue single-chunk audio', e);
                        }
                    }
                } catch (e) { console.warn('SignalR audio handler error', e); }
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
            try { _activeHookInstances.delete(_hookInstance); } catch (_) { }
        };
    }, [opts.url, opts.hubUrl, opts.meetingId]);

    // When targetLang changes, ask the server to update this connection's subscription
    useEffect(() => {
        const conn = connRef.current;
        if (!conn) return;
        // Try to re-subscribe on the existing connection. If the connection isn't ready
        // the invoke will fail and be logged, which is acceptable.
        (async () => {
            try {
                if (opts.meetingId) {
                    try { await conn.invoke('Subscribe', opts.meetingId, opts.targetLang); } catch (e) { console.warn('Resubscribe invoke failed', e); }
                }
            } catch (e) { /* ignore */ }
        })();

        // Clear local audio playback/queues to avoid playing cached audio in the previous language
        try { if (audioBuffersRef && audioBuffersRef.current) audioBuffersRef.current.clear(); } catch (_) { }
        try { if (audioQueueRef && audioQueueRef.current) audioQueueRef.current.length = 0; } catch (_) { }
    }, [opts.targetLang, opts.meetingId]);

    const unlockAudio = () => { try { unlockPendingAudio(); } catch (e) { console.warn('unlockAudio failed', e); } };

    return { lines, unlockAudio, stopAudio };
}
