import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useRealtimeCaptions } from '../utils/useRealtimeCaptions.signalr';
import * as signalR from '@microsoft/signalr';
import { API_BASE } from '../config/apiBase';

export default function CaptionsPanel(props) {

    const { url, meetingId, targetLang, currentUser } = props;
    const { lines } = useRealtimeCaptions({ url, meetingId, targetLang, currentUser });
    const containerRef = useRef(null);
    const audioRef = useRef(null);
    const [audioEnabled, setAudioEnabled] = useState(false);
    const [autoScrollEnabled, setAutoScrollEnabled] = useState(true);
    const [viewMode, setViewMode] = useState('both'); // viewMode: 'both' | 'original' | 'translated'
    const pcRef = useRef(null);

    useEffect(() => {
        const el = containerRef.current;
        if (!el) return;

        const onScroll = () => {
            // Consider the user at bottom if within 20px of the bottom
            const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight <= 20;
            setAutoScrollEnabled(atBottom);
        };

        el.addEventListener('scroll', onScroll, { passive: true });
        // initialize flag
        onScroll();

        return () => el.removeEventListener('scroll', onScroll);
    }, []);

    // Scroll to bottom whenever the lines array changes (including partial updates) and auto-scroll is enabled. useLayoutEffect ensures we measure/adjust
    // scroll before the browser paints the updated content to avoid visual jumpiness.
    useLayoutEffect(() => {
        if (!autoScrollEnabled) return;
        const el = containerRef.current;
        if (!el) return;

        // Use smooth scrolling so newly-added final captions animate into view in a visually gentle way. Fall back to instant if smooth not supported.
        requestAnimationFrame(() => {
            try {
                if (typeof el.scrollTo === 'function') {
                    try {
                        el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
                    } catch (_) {
                        el.scrollTop = el.scrollHeight;
                    }
                } else {
                    el.scrollTop = el.scrollHeight;
                }
            } catch (_) { }
        });
    }, [lines, autoScrollEnabled]);

    const connectRtc = async () => {
        const hub = new signalR.HubConnectionBuilder().withUrl(`${API_BASE}/rtc`).build();
        await hub.start();

        const pc = new RTCPeerConnection({
            iceServers: [
                { urls: 'stun:stun.l.google.com:19302' } // stun:stun.cloudflare.com
            ]
        });

        pcRef.current = pc;

        // Create a data channel before creating the offer so the resulting m-line ordering (application/datachannel vs audio) matches the server-side SIPSorcery answer
        try { pc.createDataChannel('sips'); } catch (e) { /* no-op */ }

        pc.ontrack = async (e) => {
            try {
                const audioEl = audioRef.current || document.getElementById('player');
                if (audioEl) {
                    audioEl.srcObject = e.streams[0];
                    // Attempt to play immediately. Browsers require a user gesture to allow audio autoplay;
                    // the toggle button provides that gesture. Still, call play and ignore failures.
                    try { await audioEl.play(); } catch (_) { /* autoplay blocked until user interaction */ }
                }
            } catch (_) { }
        };
        pc.addTransceiver('audio', { direction: 'recvonly' });

        pc.onicecandidate = async (e) => {
            if (e.candidate) await hub.invoke('Ice', e.candidate.candidate);
        };

        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);
        const answerSdp = await hub.invoke('Offer', offer.sdp);
        await pc.setRemoteDescription({ type: 'answer', sdp: answerSdp });
    }

    const closeRtc = async () => {
        try {
            await pcRef.current?.close();
        } catch (_) { }
        pcRef.current = null;
    }

    return (
        <div>
            <div className="controls">
                <div className="view-mode-toggle">
                    <button className={viewMode === 'both' ? 'active' : ''} onClick={() => setViewMode('both')}>Show Both</button>
                    <button className={viewMode === 'original' ? 'active' : ''} onClick={() => setViewMode('original')}>Original Only</button>
                    <button className={viewMode === 'translated' ? 'active' : ''} onClick={() => setViewMode('translated')}>Translation Only</button>
                </div>

                <div className="audio-toggle">
                    <audio id="player" ref={audioRef} autoPlay playsInline></audio>
                    <button
                        className={`icon-button ${audioEnabled ? 'audio-enabled' : 'audio-disabled'}`}
                        onClick={async () => {
                            try {
                                if (!audioEnabled) {
                                    // Ensure any user-gesture unlocking happens before creating the RTC connection
                                    await connectRtc();
                                    setAudioEnabled(true);
                                } else {
                                    await closeRtc();
                                    setAudioEnabled(false);
                                }
                            } catch (e) {
                                console.warn('toggle audio failed', e);
                            }
                        }}
                        aria-pressed={audioEnabled}
                        aria-label={audioEnabled ? 'Disable audio' : 'Enable audio'}
                    >
                        <span className="mic-icon" aria-hidden>
                            {audioEnabled ? (
                                <svg width="17" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden>
                                    <path d="M12 14a3 3 0 0 0 3-3V7a3 3 0 0 0-6 0v4a3 3 0 0 0 3 3z" stroke="#1F5FBF" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                    <path d="M19 11a7 7 0 0 1-14 0" stroke="#1F5FBF" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                    <path d="M12 18v3" stroke="#1F5FBF" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                    <path d="M9 21h6" stroke="#1F5FBF" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                </svg>
                            ) : (
                                <svg width="17" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden>
                                    <path d="M12 14a3 3 0 0 0 3-3V7a3 3 0 0 0-6 0v4a3 3 0 0 0 3 3z" stroke="#666" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                    <path d="M19 11a7 7 0 0 1-14 0" stroke="#666" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                    <path d="M12 18v3" stroke="#666" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                    <path d="M9 21h6" stroke="#666" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                    <path d="M4 4L20 20" stroke="#B00020" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                </svg>
                            )}
                        </span>
                    </button>
                </div>
            </div>
            <div className="captions" ref={containerRef}>
                {(lines || []).map((l, i) => {
                    const rawText = l.text ?? '';
                    const speaker = l.speaker ?? '';
                    const original = rawText[l.sourceLang] ?? rawText.en ?? '';
                    const translated = rawText[targetLang] ?? rawText.en ?? '';

                    return (
                        <div
                            key={`${l.startMs}-${l.endMs}-${i}`}
                            data-start-ms={l.realStartMs}
                            data-speaker-id={l.speakerId}
                            className={l.isFinal ? 'caption-block final' : 'caption-block partial'}
                        >
                            <div className="caption-speaker">[{speaker}]</div>
                            {viewMode !== 'translated' && (
                                <div className="caption-line original">
                                    <span className="label">(orig)</span>
                                    <span className="text">{original}</span>
                                </div>
                            )}
                            {viewMode !== 'original' && (
                                <div className="caption-line translated">
                                    <span className="label">(tran)</span>
                                    <span className="text">{translated}</span>
                                </div>
                            )}
                        </div>
                    );
                })}
            </div>
        </div>
    );
};
