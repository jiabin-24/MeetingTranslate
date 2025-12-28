import * as React from "react";
import * as microsoftTeams from "@microsoft/teams-js";
import {
    BrowserRouter,
    Route,
    Routes
} from 'react-router-dom';
import AppInMeeting from "../components/app-in-meeting";
import Configure from "../components/configure";

export const AppRoute = () => {
    React.useEffect(() => {
        microsoftTeams.app
            .initialize()
            .then(() => {
                console.log("App.js: initializing client SDK initialized");
                microsoftTeams.app.notifyAppLoaded();
                microsoftTeams.app.notifySuccess();
            })
            .catch((error) => console.error(error));
    }, []);

    return (
        <BrowserRouter>
            <Routes>
                <Route path="/configure" element={<Configure />} />
                <Route path="/appInMeeting" element={<AppInMeeting />} />
            </Routes>
        </BrowserRouter>
    );
};