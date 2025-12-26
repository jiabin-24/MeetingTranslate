// <copyright file="app-in-meeting.tsx" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
// </copyright>

import { useEffect } from "react";
import * as microsoftTeams from "@microsoft/teams-js";
import $ from "jquery";
import Todo from "./todo";

// Handles redirection after successful/failure sign in attempt.
const AppInMeeting = props => {
    useEffect(() => {
        microsoftTeams.app.initialize().then(() => {
            microsoftTeams.app.getContext().then((context) => {

                if (context.page.frameContext === "sidePanel") {
                    // Adding and removing classes based on screen width, to show app in stage view and in side panel
                    $("#todo").addClass("grid-item-sidepanel");
                    $("#todo").removeClass("grid-item");
                    $("#boardDiv").addClass("chat-window-sidepanel");
                    $("#boardDiv").removeClass("chat-window");
                }
                else {
                    // Adding and removing classes based on screen width, to show app in stage view and in side panel
                    $("#todo").addClass("grid-item");
                    $("#todo").removeClass("grid-item-sidepanel");
                    $("#boardDiv").addClass("chat-window");
                    $("#boardDiv").removeClass("chat-window-sidepanel");
                }
            });
        });
    }, []);

    // Share the content to meeting stage view.
    const shareSpecificAppContent = (partName) => {
        let appContentUrl = "";
        microsoftTeams.app.getContext().then((context) => {
            appContentUrl = `${window.location.origin}/todoView?meetingId=${context.meeting.id}`;
            microsoftTeams.meeting.shareAppContentToStage((err, result) => {
                if (result) {
                    console.log(result);
                }
                if (err) {
                    alert(JSON.stringify(err))
                }
            }, appContentUrl, {
                sharingProtocol: microsoftTeams.meeting.SharingProtocol.ScreenShare
            });
        });
    };

    return (
        <div id="chatSection" className="theme-light">
            <div className="label">
                Sprint Status
            </div>
            <div id="boardDiv" className="chat-window">
                <div className="theme-light">
                    <Todo shareSpecificAppContent={shareSpecificAppContent} />
                </div>
            </div>
        </div>
    );
};

export default AppInMeeting;