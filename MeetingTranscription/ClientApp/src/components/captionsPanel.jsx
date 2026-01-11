import React, { useEffect, useRef, useState } from 'react';
import { useRealtimeCaptions } from '../utils/useRealtimeCaptions';

export default function CaptionsPanel(props) {

    const { url, meetingId, targetLang } = props;
    const { lines } = useRealtimeCaptions({ url, meetingId, targetLang });
    const containerRef = useRef(null);
    const [autoScrollEnabled, setAutoScrollEnabled] = useState(true);
    // viewMode: 'both' | 'original' | 'translated'
    const [viewMode, setViewMode] = useState('both');

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

    useEffect(() => {
        if (!autoScrollEnabled) return;
        const el = containerRef.current;
        if (!el) return;
        requestAnimationFrame(() => {
            el.scrollTop = el.scrollHeight;
        });
    }, [(lines || []).length, autoScrollEnabled]);

    return (
        <div>
            <div className="view-mode-toggle">
                <button className={viewMode === 'both' ? 'active' : ''} onClick={() => setViewMode('both')}>Show Both</button>
                <button className={viewMode === 'original' ? 'active' : ''} onClick={() => setViewMode('original')}>Original Only</button>
                <button className={viewMode === 'translated' ? 'active' : ''} onClick={() => setViewMode('translated')}>Translation Only</button>
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
