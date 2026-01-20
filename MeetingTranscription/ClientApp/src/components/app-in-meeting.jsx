import { useEffect, useState } from "react";
import { API_BASE } from '../config/apiBase';
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
    var host = API_BASE === '' ? window.location.host : API_BASE.replace(/^https?:\/\//, '');
    const wsUrl = `https://${host}/captionHub`;

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