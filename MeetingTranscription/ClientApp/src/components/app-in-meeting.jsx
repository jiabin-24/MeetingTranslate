import { useEffect, useState } from "react";
import * as microsoftTeams from "@microsoft/teams-js";
import CaptionsPanel from "./captionsPanel";

// Handles redirection after successful/failure sign in attempt.
const AppInMeeting = props => {
    const [meetingId, setMeetingId] = useState(null);
    const [currentUser, setCurrentUser] = useState(null);

    useEffect(() => {
        microsoftTeams.app.initialize().then(() => {
            microsoftTeams.app.getContext().then((context) => {
                if (context && context.chat && context.chat.id) {
                    setMeetingId(context.chat.id);
                    setCurrentUser(context.user);
                }
            });
        });
    }, []);

    const [targetLang, setTargetLang] = useState('zh-Hans');

    const wsProtocol = window.location.protocol === "https:" ? "wss:" : "ws:";
    const wsUrl = `${wsProtocol}//${window.location.host}/captionHub`;
    //const wsUrl = 'wss://localhost:9441/realtime'; // For local testing with self-signed certs

    return (
        <div className="captions-panel-container">
            <div className="language-switcher">
                <span>Translate to</span>
                <select value={targetLang} onChange={e => setTargetLang(e.target.value)}>
                    <option value="zh-Hans">中文 (Chinese)</option>
                    <option value="en">English</option>
                </select>
            </div>
            {meetingId ? (
                <CaptionsPanel
                    url={wsUrl}
                    meetingId={meetingId}
                    targetLang={targetLang}
                    currentUser={currentUser}
                />
            ) : (
                <div className="loading-meeting">Waiting for meeting context...</div>
            )}
        </div>
    );
};

export default AppInMeeting;