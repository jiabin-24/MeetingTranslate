import * as React from "react";
import {
    BrowserRouter,
    Route,
    Routes
} from 'react-router-dom';
const AppInMeeting = React.lazy(() => import("../components/app-in-meeting"));
const Configure = React.lazy(() => import("../components/configure"));

export const AppRoute = () => {
    React.useEffect(() => {
        (async () => {
            try {
                const microsoftTeams = await import('@microsoft/teams-js');
                await microsoftTeams.app.initialize();
                console.log("App.js: initializing client SDK initialized");
                microsoftTeams.app.notifyAppLoaded();
                microsoftTeams.app.notifySuccess();
            } catch (error) {
                console.error('Failed to initialize Teams SDK', error);
            }
        })();
    }, []);

    return (
        <BrowserRouter>
            <React.Suspense fallback={<div style={{padding:20}}>Loading…</div>}>
                <Routes>
                    <Route path="/configure" element={<Configure />} />
                    <Route path="/appInMeeting" element={<AppInMeeting />} />
                </Routes>
            </React.Suspense>
        </BrowserRouter>
    );
};