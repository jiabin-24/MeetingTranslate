import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useRealtimeCaptions } from '../utils/useRealtimeCaptions.signalr';

export default function CaptionsPanel(props) {

    const { url, meetingId, targetLang, currentUser } = props;
    const { lines, unlockAudio, stopAudio } = useRealtimeCaptions({ url, meetingId, targetLang, currentUser });
    const containerRef = useRef(null);
    const [audioEnabled, setAudioEnabled] = useState(false);
    const [autoScrollEnabled, setAutoScrollEnabled] = useState(true);
    // viewMode: 'both' | 'original' | 'translated'
    const [viewMode, setViewMode] = useState('both');
    const [audioStatus, setAudioStatus] = useState('idle'); // 'idle'|'queued'|'playing'

    useEffect(() => {
        const handler = (ev) => {
            const s = ev && ev.detail && ev.detail.status ? ev.detail.status : 'idle';
            setAudioStatus(s);
        };
        window.addEventListener('realtime-audio-status', handler);
        return () => window.removeEventListener('realtime-audio-status', handler);
    }, []);

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

    // Scroll to bottom whenever the lines array changes (including partial updates)
    // and auto-scroll is enabled. useLayoutEffect ensures we measure/adjust
    // scroll before the browser paints the updated content to avoid visual
    // jumpiness.
    useLayoutEffect(() => {
        if (!autoScrollEnabled) return;
        const el = containerRef.current;
        if (!el) return;

        // Use smooth scrolling so newly-added final captions animate into view
        // in a visually gentle way. Fall back to instant if smooth not supported.
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


    return (
        <div>
            <div className="controls">
                <div className="view-mode-toggle">
                    <button className={viewMode === 'both' ? 'active' : ''} onClick={() => setViewMode('both')}>Show Both</button>
                    <button className={viewMode === 'original' ? 'active' : ''} onClick={() => setViewMode('original')}>Original Only</button>
                    <button className={viewMode === 'translated' ? 'active' : ''} onClick={() => setViewMode('translated')}>Translation Only</button>
                </div>

                <div className="audio-toggle">
                    <button
                        className={`icon-button ${audioEnabled ? 'audio-enabled' : 'audio-disabled'}`}
                        onClick={() => {
                            try {
                                if (!audioEnabled) {
                                    unlockAudio();
                                    setAudioEnabled(true);
                                } else {
                                    stopAudio();
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
                            data-speakerId={l.speakerId}
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
