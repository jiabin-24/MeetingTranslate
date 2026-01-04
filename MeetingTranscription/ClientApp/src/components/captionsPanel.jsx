import React, { useEffect, useRef, useState } from 'react';
import { useRealtimeCaptions } from '../utils/useRealtimeCaptions';

export default function CaptionsPanel(props) {

    const { url, token, meetingId, targetLang } = props;
    const { lines } = useRealtimeCaptions({ url, token, meetingId, targetLang });
    const containerRef = useRef(null);
    const [autoScrollEnabled, setAutoScrollEnabled] = useState(true);

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
            <div className="captions" ref={containerRef}>
                {(lines || []).map((l, i) => {
                    const rawText = l.text ?? l.Text ?? '';
                    const speaker = l.speaker ?? l.Speaker ?? '';
                    let text = rawText[targetLang] ?? rawText.en ?? Object.values(rawText)[0] ?? '';

                    return (
                        <div
                            key={`${l.startMs}-${l.endMs}-${i}`}
                            className={l.isFinal ? 'line final' : 'line partial'}
                        >
                            {`[${speaker}] `}{text}
                        </div>
                    );
                })}
            </div>
        );
};
