import React from 'react';
import { useRealtimeCaptions } from '../utils/useRealtimeCaptions';

export default function CaptionsPanel(props) {

    const { url, token, meetingId, targetLang } = props;
    const { lines } = useRealtimeCaptions({ url, token, meetingId, targetLang });

    return (
        <div className="captions">
            {lines.map((l, i) => (
                <div
                    key={`${l.StartMs}-${l.EndMs}-${i}`}
                    className={l.IsFinal ? 'line final' : 'line partial'}
                >
                    {l.Speaker ? `[${l.Speaker}] ` : ''}{l.Text}
                </div>
            ))}
        </div>
    );
};
