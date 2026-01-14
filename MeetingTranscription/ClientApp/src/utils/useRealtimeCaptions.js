
import { useEffect, useRef, useState } from 'react';

// Module-level queue and unlock state to handle browser autoplay restrictions.
// Each entry is { blob, url }
const _pendingAudioBlobs = [];
let _audioUnlocked = false;
let _interactionHandlersInstalled = false;
let _suppressPlayback = false;
// Shared AudioContext for decoding/playing when <audio> can't handle the blob
let _audioCtx = null;
// Currently playing element / source so we can stop it
let _currentAudioEl = null;
let _currentAudioUrl = null;
let _currentAudioSource = null; // AudioBufferSourceNode
// Track all created HTMLAudioElements so we can stop leaked ones too
const _activeAudioEls = new Set();
// Track active hook instances so stopAudio can clear their internal queues
const _activeHookInstances = new Set();

// Stop current HTMLAudioElement or WebAudio source but do NOT clear the pending queue.
function stopCurrentPlayback() {
    try {
        try { console.debug('stopCurrentPlayback invoked'); } catch (_) {}
        // Stop any tracked HTMLAudioElements aggressively
        try {
            for (const a of Array.from(_activeAudioEls)) {
                try { a.pause(); } catch (_) {}
                try { a.muted = true; } catch (_) {}
                try { a.volume = 0; } catch (_) {}
                try { a.currentTime = 0; } catch (_) {}
                try { a.srcObject = null; } catch (_) {}
                try { a.src = ''; } catch (_) {}
                try { a.load(); } catch (_) {}
                try { _activeAudioEls.delete(a); } catch (_) {}
            }
            _currentAudioEl = null;
        } catch (_) {}
        // Also try to stop any <audio> elements in the document in case some were created elsewhere
        try {
            const els = Array.from(document.getElementsByTagName('audio'));
            for (const a of els) {
                try { a.pause(); } catch (_) {}
                try { a.muted = true; } catch (_) {}
                try { a.volume = 0; } catch (_) {}
                try { a.currentTime = 0; } catch (_) {}
                try { a.srcObject = null; } catch (_) {}
                try { a.src = ''; } catch (_) {}
                try { a.load(); } catch (_) {}
            }
        } catch (_) {}
        if (_currentAudioUrl) {
            try { URL.revokeObjectURL(_currentAudioUrl); } catch (_) {}
            _currentAudioUrl = null;
        }
        // cancel speech synthesis if any
        try { if (window.speechSynthesis && typeof window.speechSynthesis.cancel === 'function') window.speechSynthesis.cancel(); } catch (_) {}
        if (_currentAudioSource) {
            try { _currentAudioSource.onended = null; } catch (_) {}
            try { _currentAudioSource.stop(); } catch (_) {}
            try { _currentAudioSource.disconnect(); } catch (_) {}
            _currentAudioSource = null;
        }
    } catch (e) {
        console.warn('stopCurrentPlayback failed', e);
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
            ctx.resume().catch(() => {});
        }
    } catch {}

    // Play queued blobs sequentially. Each entry should be { blob, url }.
    while (_pendingAudioBlobs.length) {
        const entry = _pendingAudioBlobs.shift();
        const blob = entry && entry.blob ? entry.blob : entry;
        let url = entry && entry.url ? entry.url : null;
        if (!url) url = URL.createObjectURL(blob);
        try {
            // notify UI that playback is starting
            try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'playing' } })); } catch (_) {}
            await playAudioUrl(url);
            try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'idle' } })); } catch (_) {}
        } catch (e) {
            // If it still fails, just drop it
            console.warn('Playback of queued audio failed', e);
        } finally {
            try { URL.revokeObjectURL(url); } catch {}
        }
    }
}

// Stop playback and clear queued audio
function stopAudio() {
    try {
        try { console.debug('stopAudio invoked'); } catch (_) {}
        // prevent any further incoming audio from starting
        _suppressPlayback = true;
        // stop any currently playing audio (HTMLAudioElements and WebAudio sources)
        stopCurrentPlayback();
        // clear pending queue: revoke any object URLs held in the pending queue
        try {
            for (const entry of _pendingAudioBlobs) {
                if (entry && entry.url) {
                    try { URL.revokeObjectURL(entry.url); } catch (_) {}
                }
            }
        } catch (_) {}
        _pendingAudioBlobs.length = 0;
        // Clear any partially assembled buffers/queues in active hook instances
        try {
            for (const inst of Array.from(_activeHookInstances)) {
                try { if (inst.audioBuffersRef && inst.audioBuffersRef.current) inst.audioBuffersRef.current.clear(); } catch (_) {}
                try { if (inst.audioQueueRef && inst.audioQueueRef.current) inst.audioQueueRef.current.length = 0; } catch (_) {}
            }
        } catch (_) {}
        try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'idle' } })); } catch (_) {}
    } catch (e) {
        console.warn('stopAudio failed', e);
    }
}

// Play an audio URL via HTMLAudioElement and return a promise that resolves when playback ends.
function playAudioUrl(url) {
    return new Promise((resolve, reject) => {
        try {
            // ensure any previously playing audio is stopped first
            stopCurrentPlayback();
            const audio = new Audio(url);
            _activeAudioEls.add(audio);
            _currentAudioEl = audio;
            _currentAudioUrl = url;
            audio.autoplay = true;
            const cleanup = () => {
                audio.removeEventListener('ended', onEnd);
                audio.removeEventListener('error', onError);
                try { _activeAudioEls.delete(audio); } catch (_) {}
                if (_currentAudioEl === audio) _currentAudioEl = null;
                if (_currentAudioUrl === url) { try { URL.revokeObjectURL(_currentAudioUrl); } catch (_) {} ; _currentAudioUrl = null; }
            };
            const onEnd = () => { cleanup(); resolve(); };
            const onError = (e) => { cleanup(); reject(e || new Error('Audio playback error')); };
            audio.addEventListener('ended', onEnd);
            audio.addEventListener('error', onError);
            const p = audio.play();
            if (_suppressPlayback) {
                try { cleanup(); } catch (_) {}
                return;
            }
            if (p && typeof p.catch === 'function') {
                p.catch(err => {
                    cleanup();
                    reject(err);
                });
            }
        } catch (e) {
            reject(e);
        }
    });
}

export function useRealtimeCaptions(opts) {
    const [lines, setLines] = useState([]);
    const wsRef = useRef(null);
    const audioBuffersRef = useRef(new Map());
    const audioQueueRef = useRef([]);
    // register this hook instance so module-level stopAudio can clear its queues
    const _hookInstance = { audioBuffersRef, audioQueueRef };
    try { _activeHookInstances.add(_hookInstance); } catch (_) {}
    const reconnectRef = useRef(null);
    const backoffRef = useRef(1000); // 指数退避起始 1s

    useEffect(() => {
        let abort = false;
        // Fetch initial captions over HTTP when meetingId is available
        if (opts.meetingId) {
            (async () => {
                try {
                    const url = `https://localhost:9441/api/meeting/getMeetingCaptions?threadId=${encodeURIComponent(opts.meetingId)}`;
                    const resp = await fetch(url, { method: 'GET', headers: { 'Accept': 'application/json' }, mode: 'cors', credentials: 'include' });
                    if (!resp.ok) return;
                    const data = await resp.json();
                    if (abort) return;
                    // Expect data to be an array of caption objects; normalize keys if needed
                    if (Array.isArray(data)) {
                        // Replace local lines with server-provided initial captions.
                        // This prevents duplicates when switching targetLang which
                        // would otherwise re-fetch and merge the same items.
                        setLines(() => sortByTime(data));
                    }
                } catch (e) {
                    // ignore fetch errors
                }
            })();
        }
        let closed = false;

        const connect = () => {
            if (closed) return;

            // 如果已有连接正在建立或已打开，则不要重复创建新的连接（防止重复认证/订阅）
            const existing = wsRef.current;
            if (existing && (existing.readyState === WebSocket.CONNECTING || existing.readyState === WebSocket.OPEN)) {
                return;
            }

            const ws = new WebSocket(opts.url);
            // receive binary frames as ArrayBuffer for audio
            ws.binaryType = 'arraybuffer';
            wsRef.current = ws;

            ws.addEventListener('open', () => {
                // 首条消息做鉴权 + 订阅
                // 可能在某些环境下 open 被触发/调用多次，添加幂等保护，确保只发送一次鉴权/订阅
                if (ws.__didAuthenticate) return;
                ws.__didAuthenticate = true;
                backoffRef.current = 1000; // 重置退避
                try {
                    ws.send(JSON.stringify({ Type: 'auth', Token: null }));
                    ws.send(JSON.stringify({
                        Type: 'subscribe',
                        MeetingId: opts.meetingId,
                        TargetLang: opts.targetLang
                    }));
                } catch (e) {
                    // 如果发送失败（例如 socket 已关闭），标记为未鉴权以便重连后重试
                    ws.__didAuthenticate = false;
                    console.warn('Failed to send auth/subscribe:', e);
                }
            });

            ws.addEventListener('message', (ev) => {
                const data = ev.data;
                // text messages contain json metadata or captions
                if (typeof data === 'string') {
                    if (data === 'ping') return; // 心跳
                    try {
                        const msg = JSON.parse(data);
                        if (msg && msg.type === 'caption') {
                            setLines(prev => mergeCaptions(prev, msg));
                        } else if (msg && msg.type === 'audio') {
                            // Prefer an explicit audioId to correlate metadata->binary; fall back to meetingId
                            const audioKey = msg.audioId || msg.meetingId || (`audio:${Date.now()}`);
                            // server will send a metadata JSON followed by one or more binary frames
                            audioBuffersRef.current.set(audioKey, { contentType: msg.contentType || '', chunks: [], isFinal: !!msg.isFinal, headerHex: msg.headerHex });
                            audioQueueRef.current.push(audioKey);
                        }
                    } catch {
                        // 忽略解析错误
                    }
                    return;
                }

                // binary frames: audio payloads
                try {
                    const ab = data; // ArrayBuffer
                    // If audio not unlocked yet or playback suppressed, drop incoming binary audio
                    if (!_audioUnlocked || _suppressPlayback) {
                        return;
                    }
                    // Correlate this binary frame with the earliest queued audio metadata
                    let foundKey = null;
                    if (audioQueueRef.current.length > 0) {
                        foundKey = audioQueueRef.current.shift();
                    } else if (opts.meetingId && audioBuffersRef.current.has(opts.meetingId)) {
                        foundKey = opts.meetingId;
                    } else if (audioBuffersRef.current.size === 1) {
                        foundKey = Array.from(audioBuffersRef.current.keys())[0];
                    }
                    if (!foundKey) return;
                    const entry = audioBuffersRef.current.get(foundKey);
                    if (!entry) return;
                    entry.chunks.push(ab);

                    if (entry.isFinal) {
                        // assemble and play (handle autoplay restrictions)
                        // merge chunks into a single Uint8Array
                        let totalLen = 0;
                        for (const c of entry.chunks) totalLen += (c.byteLength || 0);
                        const merged = new Uint8Array(totalLen);
                        let o = 0;
                        for (const c of entry.chunks) { const u = new Uint8Array(c); merged.set(u, o); o += u.length; }

                        // try to find common container headers: 'RIFF' or 'ID3' or MP3 frame (0xff Ex)
                        const findSeq = (arr, seq) => {
                            for (let i = 0; i + seq.length <= arr.length; i++) {
                                let ok = true;
                                for (let j = 0; j < seq.length; j++) if (arr[i + j] !== seq[j]) { ok = false; break; }
                                if (ok) return i;
                            }
                            return -1;
                        };

                        const riffIdx = findSeq(merged, [0x52,0x49,0x46,0x46]); // 'RIFF'
                        const id3Idx = findSeq(merged, [0x49,0x44,0x33]); // 'ID3'
                        let mp3Idx = -1;
                        for (let i = 0; i + 1 < merged.length; i++) {
                            if (merged[i] === 0xFF && (merged[i+1] & 0xE0) === 0xE0) { mp3Idx = i; break; }
                        }

                        let blob = null;
                        if (riffIdx >= 0) {
                            if (riffIdx > 0) console.warn('Found RIFF at offset', riffIdx, 'trimming leading bytes');
                            const slice = merged.slice(riffIdx);
                            blob = new Blob([slice], { type: entry.contentType || 'audio/wav' });
                        } else if (id3Idx >= 0 || mp3Idx >= 0 || (entry.contentType && entry.contentType.includes('mpeg'))) {
                            const idx = id3Idx >= 0 ? id3Idx : (mp3Idx >= 0 ? mp3Idx : 0);
                            if (idx > 0) console.warn('Found MP3 header at offset', idx, 'trimming leading bytes');
                            const slice = merged.slice(idx);
                            blob = new Blob([slice], { type: entry.contentType || 'audio/mpeg' });
                        } else {
                            // assume raw PCM16LE and wrap as WAV
                            console.warn('No container header found; wrapping raw PCM as WAV');
                            const wavBuf = createWavFromPcm16LE([merged.buffer], 16000, 1);
                            blob = new Blob([wavBuf], { type: 'audio/wav' });
                        }
                        // Try <audio> first. If it fails due to unsupported format, fall back to WebAudio decode.
                        const url = URL.createObjectURL(blob);
                        // stop any currently playing audio before starting this one
                        stopCurrentPlayback();
                        _currentAudioUrl = url;
                        const audio = new Audio(url);
                        // track current HTMLAudioElement so stopAudio can cancel it
                        _activeAudioEls.add(audio);
                        _currentAudioEl = audio;
                        audio.autoplay = true;
                        const cleanupLocalAudio = () => {
                            try { audio.removeEventListener('ended', onEnd); } catch (_) {}
                            try { audio.removeEventListener('error', onError); } catch (_) {}
                            if (_currentAudioEl === audio) _currentAudioEl = null;
                            if (_currentAudioUrl) { try { URL.revokeObjectURL(_currentAudioUrl); } catch (_) {} ; _currentAudioUrl = null; }
                        };
                        const onEnd = () => { try { URL.revokeObjectURL(url); } catch (_) {} ; cleanupLocalAudio(); };
                        const onError = () => { try { URL.revokeObjectURL(url); } catch (_) {} ; cleanupLocalAudio(); };
                        audio.addEventListener('ended', onEnd);
                        audio.addEventListener('error', onError);

                        const tryPlayAudioElement = () => {
                            const p = audio.play();
                                if (_suppressPlayback) {
                                    try { cleanupLocalAudio(); } catch (_) {}
                                    return;
                                }
                            if (p && typeof p.catch === 'function') {
                                p.catch(async (err) => {
                                    // If it's an autoplay/block error, queue the blob
                                    if (isNotAllowedError(err)) {
                                        // keep the url so it can be played later
                                        _pendingAudioBlobs.push({ blob, url });
                                        try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'queued' } })); } catch (_) {}
                                        setupInteractionUnlock();
                                        return;
                                    }

                                    // If it's NotSupportedError or similar, try WebAudio
                                    const name = err && err.name ? err.name : '';
                                    const msg = String(err && err.message ? err.message : '');
                                    if (name === 'NotSupportedError' || /no supported source/.test(msg) || /format/.test(msg)) {
                                        // clean up the local audio element and revoke the URL before falling back
                                        try { cleanupLocalAudio(); } catch (_) {}
                                        try { URL.revokeObjectURL(url); } catch (_) {}
                                        try {
                                            try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'playing' } })); } catch (_) {}
                                            await playChunksWithWebAudio(entry.chunks);
                                            try { window.dispatchEvent(new CustomEvent('realtime-audio-status', { detail: { status: 'idle' } })); } catch (_) {}
                                        } catch (webaudioErr) {
                                            console.warn('WebAudio fallback failed', webaudioErr);
                                        }
                                    } else {
                                        try { cleanupLocalAudio(); } catch (_) {}
                                        console.warn('Audio play failed', err);
                                    }
                                });
                            }
                        };

                        if (_audioUnlocked) {
                            tryPlayAudioElement();
                        } else {
                            // Attempt immediate play; if blocked we'll queue and install unlock handlers
                            tryPlayAudioElement();
                        }
                        audioBuffersRef.current.delete(foundKey);
                    }
                } catch (e) {
                    console.warn('Error handling binary audio frame', e);
                }
            });

            ws.addEventListener('close', () => {
                if (closed) return;
                if (reconnectRef.current != null) return;
                reconnectRef.current = window.setTimeout(() => {
                    reconnectRef.current = null;
                    backoffRef.current = Math.min(backoffRef.current * 2, 15000);
                    connect();
                }, backoffRef.current);
            });

            ws.addEventListener('error', () => {
                // 依旧由 close 触发重连
            });
        };

        connect();

        return () => {
            closed = true;
            abort = true;
            if (wsRef.current && wsRef.current.readyState === WebSocket.OPEN) {
                wsRef.current.close();
            }
            if (reconnectRef.current) {
                window.clearTimeout(reconnectRef.current);
            }
            try { _activeHookInstances.delete(_hookInstance); } catch (_) {}
        };
    }, [opts.url, opts.meetingId]);

    // Expose an explicit unlock function so UI can call it after user gesture
    const unlockAudio = () => {
        try {
            unlockPendingAudio();
        } catch (e) {
            console.warn('unlockAudio failed', e);
        }
    };

    return { lines, unlockAudio, stopAudio };
}

// Debug helper: call from console to forcibly stop any playback managed here.
try {
    if (typeof window !== 'undefined' && !window.__realtimeStopAudio) {
        window.__realtimeStopAudio = () => {
            try { stopCurrentPlayback(); } catch (_) {}
            try { stopAudio(); } catch (_) {}
        };
    }
} catch (_) {}

// 将 final 片段覆盖同时间窗的 partial，减少闪烁
function mergeCaptions(prev, incoming) {
    const isFinal = !!incoming.isFinal;
    const hasWindow = incoming.startMs != null && incoming.endMs != null;

    if (isFinal && hasWindow) {
        const filtered = prev.filter(l => !(l.startMs === incoming.startMs && l.endMs === incoming.endMs));
        return sortByTime([...filtered, incoming]);
    } else {
        return sortByTime([...prev, incoming]);
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

// WebAudio fallback: concatenate ArrayBuffers and decode + play
async function playChunksWithWebAudio(chunks) {
    const ctx = ensureAudioContext();
    if (!ctx) throw new Error('WebAudio not supported');

    // Concatenate ArrayBuffers
    let totalLen = 0;
    for (const c of chunks) totalLen += (c.byteLength || 0);
    const merged = new Uint8Array(totalLen);
    let offset = 0;
    for (const c of chunks) {
        const u8 = new Uint8Array(c);
        merged.set(u8, offset);
        offset += u8.length;
    }

    // Try decoding (may throw if format unsupported)
    try {
        const audioBuffer = await ctx.decodeAudioData(merged.buffer.slice(0));
        const src = ctx.createBufferSource();
        _currentAudioSource = src;
        src.buffer = audioBuffer;
        src.connect(ctx.destination);
        return new Promise((resolve) => {
            src.onended = () => { try { src.disconnect(); } catch {} ; _currentAudioSource = null; resolve(); };
            src.start(0);
        });
    } catch (e) {
        // Log first bytes to help identify format header
        try {
            const header = Array.from(new Uint8Array(merged.buffer.slice(0, 12))).map(b => b.toString(16).padStart(2, '0')).join(' ');
            console.warn('WebAudio decode failed; first bytes:', header);
        } catch (_) {}
        throw new Error('Unable to decode audio data');
    }
}
