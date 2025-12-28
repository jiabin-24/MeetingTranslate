import { useEffect } from "react";
import * as microsoftTeams from "@microsoft/teams-js";
import CaptionsPanel from "./captionsPanel";

// Handles redirection after successful/failure sign in attempt.
const AppInMeeting = props => {
    useEffect(() => {
        microsoftTeams.app.initialize().then(() => {
            microsoftTeams.app.getContext().then((context) => {
                if (context.page.frameContext === "sidePanel") {
                    // Adding and removing classes based on screen width, to show app in stage view and in side panel

                }
                else {
                    // Adding and removing classes based on screen width, to show app in stage view and in side panel

                }
            });
        });
    }, []);

    // 实际项目中从登录/会议上下文拿 token & meetingId
    const token = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1c2VyXzEyMyIsIm1lZXRpbmdJZCI6Im1lZXRpbmdfYWJjIiwianRpIjoiMTIzNDU2NzgiLCJleHAiOjE3MzUwMzg1MjcsImlzcyI6InlvdXItYXBwIiwiYXVkIjoieW91ci1jbGllbnQifQ.7CX1V8oP9g4FRTN0d8qJ4knT4M0k5d_RSSG5DH0rFxw';
    const meetingId = 'demo-001';
    const targetLang = 'zh';

    const wsProtocol = window.location.protocol === "https:" ? "wss:" : "ws:";
    const wsUrl = `${wsProtocol}//${window.location.host}/realtime`;

    return (
        <div>
            <h3 style={{marginTop: "0"}}>实时字幕</h3>
            <CaptionsPanel
                url={wsUrl}
                token={token}
                meetingId={meetingId}
                targetLang={targetLang}
            />
        </div>
    );
};

export default AppInMeeting;