import React, { useEffect } from "react";
import * as microsoftTeams from "@microsoft/teams-js";

// Handles redirection after successful/failure sign in attempt.
const Configure = props => {
    useEffect(() => {
        microsoftTeams.app.initialize().then(() => {
            microsoftTeams.app.notifySuccess();
            microsoftTeams.pages.config.registerOnSaveHandler(async function (saveEvent) {
                microsoftTeams.pages.config.setConfig({
                    entityID: "Meeting Transcript Bot",
                    contentUrl: `${window.location.origin}/appInMeeting`,
                    suggestedTabName: "App in meeting",
                    websiteUrl: `${window.location.origin}/appInMeeting`,
                });
                saveEvent.notifySuccess();
                // Get context and call backend to join call
                const context = await microsoftTeams.app.getContext();
                // Call helper to notify backend to join call
                joinCallAsync(context);
            });

            microsoftTeams.pages.config.setValidityState(true);
        });
    }, []);

    const joinCallAsync = async (context) => {
        try {
            // Try to extract a direct join link from known fields
            let joinUrl = null;
            const meetingId = context?.chat?.id;
            const tenantId = context?.user?.tenant?.id;
            const userOid = context?.user?.id;

            if (meetingId && tenantId && userOid) {
                try {
                    const meetingSegment = encodeURIComponent(`19:meeting_${meetingId}@thread.v2`);
                    const contextParam = encodeURIComponent(JSON.stringify({ Tid: tenantId, Oid: userOid }));
                    joinUrl = `https://teams.microsoft.com/l/meetup-join/${meetingSegment}/0?context=${contextParam}`;
                } catch (e) {
                    console.warn('Failed to construct joinUrl', e);
                }
            }
            const res = await fetch('/Calls', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ joinUrl: joinUrl }),
            });
            if (!res.ok) {
                console.error('JoinCallAsync failed', res.status, res.statusText);
            }
        } catch (err) {
            console.error('joinCallAsync error', err);
        }
    };

    return (
        <header className="header">
            <div className="header-inner-container">
                <h2>Meeting Transcript Bot</h2>
            </div>
        </header>
    );
};

export default Configure;