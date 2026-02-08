
import { useEffect, useRef, useState } from 'react';
import { API_BASE } from '../config/apiBase';

// Module-level queue and unlock state to handle browser autoplay restrictions.

// Maximum captions to keep in the client's in-memory list
const MAX_CAPTIONS = 100;

export function useRealtimeCaptions(opts) {
    const [lines, setLines] = useState([]);
    const wsRef = useRef(null);
    const reconnectRef = useRef(null);
    const backoffRef = useRef(1000); // 指数退避起始 1s

    useEffect(() => {
        let abort = false;
        // Fetch initial captions over HTTP when meetingId is available
        if (opts.meetingId) {
            (async () => {
                try {
                    const url = `${API_BASE}/api/meeting/getMeetingCaptions?threadId=${encodeURIComponent(opts.meetingId)}`;
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
                        }
                    } catch {
                        // 忽略解析错误
                    }
                    return;
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
        };
    }, [opts.url, opts.meetingId]);

    // When targetLang changes, ask the server to update this connection's subscription
    useEffect(() => {
        try {
            const ws = wsRef.current;
            if (!ws) return;
            if (ws.readyState !== WebSocket.OPEN) return;

            // Send subscribe message on the existing WebSocket connection
            ws.send(JSON.stringify({ Type: 'subscribe', MeetingId: opts.meetingId, TargetLang: opts.targetLang }));
        } catch (e) {
            console.warn('WebSocket resubscribe failed', e);
        }
    }, [opts.targetLang, opts.meetingId]);

    return { lines };
}

// 将 final 片段覆盖同时间窗的 partial，减少闪烁
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
        // instead of appending a new one each time. Match by speakerId and sourceLang
        // so interim updates replace the previous partial from the same speaker/language pair.
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
        const next = [...prev, incoming];
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
