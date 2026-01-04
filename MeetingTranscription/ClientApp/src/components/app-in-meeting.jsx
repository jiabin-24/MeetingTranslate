import { useEffect, useState } from "react";
import * as microsoftTeams from "@microsoft/teams-js";
import CaptionsPanel from "./captionsPanel";

// Handles redirection after successful/failure sign in attempt.
const AppInMeeting = props => {
    const [meetingId, setMeetingId] = useState(null);

    useEffect(() => {
        microsoftTeams.app.initialize().then(() => {
            microsoftTeams.app.getContext().then((context) => {
                if (context && context.chat && context.chat.id) {
                    setMeetingId(context.chat.id);
                }
            });
        });
    }, []);

    // 实际项目中从登录/会议上下文拿 token & meetingId
    const token = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1c2VyXzEyMyIsIm1lZXRpbmdJZCI6Im1lZXRpbmdfYWJjIiwianRpIjoiMTIzNDU2NzgiLCJleHAiOjE3MzUwMzg1MjcsImlzcyI6InlvdXItYXBwIiwiYXVkIjoieW91ci1jbGllbnQifQ.7CX1V8oP9g4FRTN0d8qJ4knT4M0k5d_RSSG5DH0rFxw';
    const [targetLang, setTargetLang] = useState('zh');

    const wsProtocol = window.location.protocol === "https:" ? "wss:" : "ws:";
    const wsUrl = `${wsProtocol}//${window.location.host}/realtime`;

    return (
        <div className="captions-panel-container">
            <div className="language-switcher">
                <span>Translate to</span>
                <select value={targetLang} onChange={e => setTargetLang(e.target.value)}>
                    <option value="zh">中文 (Chinese)</option>
                    <option value="en">English</option>
                </select>
            </div>
            {meetingId ? (
                <CaptionsPanel
                    url={wsUrl}
                    token={token}
                    meetingId={meetingId}
                    targetLang={targetLang}
                />
            ) : (
                <div className="loading-meeting">Waiting for meeting context...</div>
            )}
        </div>
    );
};

export default AppInMeeting;