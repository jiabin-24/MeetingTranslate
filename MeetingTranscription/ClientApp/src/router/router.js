import * as React from "react";
import * as microsoftTeams from "@microsoft/teams-js";
import {
    BrowserRouter,
    Route,
    Routes
} from 'react-router-dom';
import AppInMeeting from "../components/app-in-meeting";
import Configure from "../components/configure";
import Todo from "../components/todo";
import Home from "../components/home";

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
        <React.Fragment>
            <BrowserRouter>
                <Routes>
                    <Route path="/configure" element={<Configure />} />
                    <Route path="/appInMeeting" element={<AppInMeeting />} />
                    <Route path="/todoView" element={<Todo shareSpecificAppContent={(meetingStatus) => { }} />} />
                    <Route path="/task" element= { <Home /> }/>
                </Routes>
            </BrowserRouter>
        </React.Fragment>
    );
};