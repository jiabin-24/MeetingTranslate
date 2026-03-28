import { useEffect, useState } from "react";
import { API_BASE, USE_BYTE_DANCE } from '../config/apiBase';
import CaptionsPanel from "./captionsPanel";

// Handles redirection after successful/failure sign in attempt.
const AppInMeeting = props => {
    const [meetingId, setMeetingId] = useState(null);
    const [currentUser, setCurrentUser] = useState(null);
    const useByteDance = USE_BYTE_DANCE;
    
    useEffect(() => {
        (async () => {
            try {
                const microsoftTeams = await import('@microsoft/teams-js');
                await microsoftTeams.app.initialize();
                const context = await microsoftTeams.app.getContext();
                if (context && context.chat && context.chat.id) {
                    setMeetingId(context.chat.id);
                    setCurrentUser(context.user);
                }
            } catch (e) {
                console.warn('Failed to get Teams context', e);
            }
        })();
    }, []);

    const [sourceLang, setSourceLang] = useState(() => {
        return useByteDance ? 'zhen' : localStorage.getItem('sourceLang') || 'zh-Hans';
    });
    const [targetLang, setTargetLang] = useState(() => {
        return localStorage.getItem('targetLang') || 'en';
    });

    useEffect(() => {
        localStorage.setItem('sourceLang', sourceLang);
    }, [sourceLang]);

    useEffect(() => {
        localStorage.setItem('targetLang', targetLang);
    }, [targetLang]);

    let host = API_BASE === '' ? window.location.host : API_BASE.replace(/^https?:\/\//, '');
    const wsUrl = `https://${host}/captionHub`;

    return (
        <div className="captions-panel-container">
            <div className="language-switcher" >
                <span>Translate from</span>
                <select value={sourceLang} onChange={e => setSourceLang(e.target.value)} disabled={useByteDance}>
                    {/* <option value="">Auto Detect</option> */}
                    {useByteDance && <option value="zhen">Auto Detect</option>}
                    <option value="zh-Hans">中文 (Chinese)</option>
                    <option value="en">English</option>
                </select>
                <span>to</span>
                <select value={targetLang} onChange={e => setTargetLang(e.target.value)}>
                    {/* {useByteDance && <option value="enzh">Auto Detect</option>} */}
                    <option value="zh-Hans">中文 (Chinese)</option>
                    <option value="en">English</option>
                </select>
            </div>
            {meetingId ? (
                <CaptionsPanel
                    url={wsUrl}
                    meetingId={meetingId}
                    sourceLang={sourceLang}
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