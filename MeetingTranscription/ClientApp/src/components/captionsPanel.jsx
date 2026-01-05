import React, { useEffect, useRef, useState } from 'react';
import { useRealtimeCaptions } from '../utils/useRealtimeCaptions';

export default function CaptionsPanel(props) {

    const { url, token, meetingId, targetLang } = props;
    const { lines } = useRealtimeCaptions({ url, token, meetingId, targetLang });
    const containerRef = useRef(null);
    const [autoScrollEnabled, setAutoScrollEnabled] = useState(true);
    const [showOnlyTranslated, setShowOnlyTranslated] = useState(true);

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
            <div className="translation-toggle">
                <label>
                    <input type="checkbox" checked={showOnlyTranslated} onChange={e => setShowOnlyTranslated(e.target.checked)} /> Show translation only
                </label>
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
                            {!showOnlyTranslated && (
                                <div className="caption-line original">
                                    <span className="label">(Orig)</span>
                                    <span className="text">{original}</span>
                                </div>
                            )}
                            <div className="caption-line translated">
                                {!showOnlyTranslated && <span className="label">(Tran)</span>}
                                <span className="text">{translated}</span>
                            </div>
                        </div>
                    );
                })}
            </div>
        </div>
    );
};
