import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useRealtimeCaptions } from '../utils/useRealtimeCaptions.signalr';
const { CallClient } = require('@azure/communication-calling');
const { AzureCommunicationTokenCredential } = require('@azure/communication-common');
import { API_BASE } from '../config/apiBase';

export default function CaptionsPanel(props) {

    const { url, meetingId, targetLang, currentUser } = props;
    const { lines } = useRealtimeCaptions({ url, meetingId, targetLang, currentUser });
    const containerRef = useRef(null);
    const audioRef = useRef(null);
    const [audioEnabled, setAudioEnabled] = useState(false);
    const [audioLoading, setAudioLoading] = useState(false);
    const [autoScrollEnabled, setAutoScrollEnabled] = useState(true);
    const [viewMode, setViewMode] = useState('both'); // viewMode: 'both' | 'original' | 'translated'

    let callAgent;
    const [call, setCall] = useState(null);
    let roomId;

    useEffect(() => {
        const el = containerRef.current;
        if (!el) return;

        const onScroll = () => {
            // Consider the user at bottom if within 20px of the bottom
            const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight <= 20;
            setAutoScrollEnabled(atBottom);
        };

        el.addEventListener('scroll', onScroll, { passive: true });
        // initialize flag
        onScroll();

        return () => el.removeEventListener('scroll', onScroll);
    }, []);

    // Scroll to bottom whenever the lines array changes (including partial updates) and auto-scroll is enabled. useLayoutEffect ensures we measure/adjust
    // scroll before the browser paints the updated content to avoid visual jumpiness.
    useLayoutEffect(() => {
        if (!autoScrollEnabled) return;
        const el = containerRef.current;
        if (!el) return;

        // Use smooth scrolling so newly-added final captions animate into view in a visually gentle way. Fall back to instant if smooth not supported.
        requestAnimationFrame(() => {
            try {
                if (typeof el.scrollTo === 'function') {
                    try {
                        el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
                    } catch (_) {
                        el.scrollTop = el.scrollHeight;
                    }
                } else {
                    el.scrollTop = el.scrollHeight;
                }
            } catch (_) { }
        });
    }, [lines, autoScrollEnabled]);

    const connectRtc = async () => {
        const r = await fetch(`${API_BASE}/api/acs/addParticipant?groupId=${meetingId}&lang=${targetLang}&participantId=${currentUser.id}`, { method: 'POST' });
        const t = await r.json();

        roomId = t.roomId;
        const tokenCredential = new AzureCommunicationTokenCredential(t.participants[0].userToken);
        const callClient = new CallClient();
        callAgent = await callClient.createCallAgent(tokenCredential);

        const c = callAgent.join({ roomId: roomId }, { videoOptions: undefined });
        startCall(c);
    }

    const log = (...args) => {
        console.log(...args);
    }

    const startCall = async (c) => {
        try {
            setCall(c);
            log('[call] state=', c.state);
            if (typeof c.on === 'function') {
                c.on('stateChanged', () => log('[call] stateChanged ->', c.state));

                c.on('remoteParticipantsUpdated', (e) => {
                    log('[call] remoteParticipantsUpdated: added=', e.added?.length || 0, 'removed=', e.removed?.length || 0);
                    (e.added || []).forEach(wireParticipant);
                });
            }

            // 已经存在的 participant
            (c.remoteParticipants || []).forEach(wireParticipant);

            // 有些版本 call 也直接提供 remoteAudioStreams
            if (Array.isArray(c.remoteAudioStreams) && c.remoteAudioStreams.length) {
                log('[call] initial remoteAudioStreams.length=', c.remoteAudioStreams.length);
                attachRemoteAudioStream(c.remoteAudioStreams[0]).catch(err => log('[attach] error', err));
            }

            if (typeof c.on === 'function') {
                try {
                    c.on('remoteAudioStreamsUpdated', (e) => {
                        log('[call] remoteAudioStreamsUpdated: added=', e.added?.length || 0, 'removed=', e.removed?.length || 0);
                        if (e.added && e.added.length) {
                            attachRemoteAudioStream(e.added[0]).catch(err => log('[attach] error', err));
                        }
                    });
                } catch { }
            }
        } catch (err) {
            log(err);
        }
    }

    const attachRemoteAudioStream = async (remoteAudioStream) => {
        if (!remoteAudioStream) return;
        log('[remoteAudio] attaching stream...');

        // 1) 优先尝试 getMediaStream()
        if (typeof remoteAudioStream.getMediaStream === 'function') {
            const ms = await remoteAudioStream.getMediaStream();
            audioRef.current.srcObject = ms;
            log('[remoteAudio] attached via getMediaStream()');
            return;
        }

        // 2) 其次尝试 getMediaStreamTrack() -> MediaStream
        if (typeof remoteAudioStream.getMediaStreamTrack === 'function') {
            const track = await remoteAudioStream.getMediaStreamTrack();
            if (track) {
                const ms = new MediaStream([track]);
                audioRef.current.srcObject = ms;
                log('[remoteAudio] attached via getMediaStreamTrack()');
                return;
            }
        }

        log('[remoteAudio] cannot attach: no getMediaStream/getMediaStreamTrack found on object keys=', Object.keys(remoteAudioStream));
    }

    const wireParticipant = (participant) => {
        if (!participant) return;
        log('[participant] added:', participant.identifier);

        // 一些版本叫 audioStreams / audioStreamsUpdated；也可能叫 remoteAudioStreams/remoteAudioStreamsUpdated
        const tryHook = (streamListProp, updatedEventName) => {
            const list = participant[streamListProp];
            if (Array.isArray(list) && list.length) {
                log(`[participant] initial ${streamListProp}.length=`, list.length);
                // 取第一条先播
                attachRemoteAudioStream(list[0]).catch(err => log('[attach] error', err));
            }
            if (typeof participant.on === 'function') {
                try {
                    participant.on(updatedEventName, (e) => {
                        log(`[participant] ${updatedEventName}: added=${e.added?.length || 0}, removed=${e.removed?.length || 0}`);
                        if (e.added && e.added.length) {
                            attachRemoteAudioStream(e.added[0]).catch(err => log('[attach] error', err));
                        }
                    });
                    log(`[participant] hooked ${updatedEventName}`);
                    return true;
                } catch { }
            }
            return false;
        };

        // 先试常见组合
        if (tryHook('audioStreams', 'audioStreamsUpdated')) return;
        if (tryHook('remoteAudioStreams', 'remoteAudioStreamsUpdated')) return;
        // 兜底：把对象结构打印出来便于你按实际字段改
        log('[participant] cannot auto-hook streams. Keys=', Object.keys(participant));
    }

    const closeRtc = async () => {
        if (!call) {
            log('no active call to close');
            return;
        }
        await call.hangUp({ forEveryone: false });
        setCall(null);
        audioRef.current.srcObject = null;
    }

    return (
        <div>
            <div className="controls">
                <div className="view-mode-toggle">
                    <button className={viewMode === 'both' ? 'active' : ''} onClick={() => setViewMode('both')}>Show Both</button>
                    <button className={viewMode === 'original' ? 'active' : ''} onClick={() => setViewMode('original')}>Original Only</button>
                    <button className={viewMode === 'translated' ? 'active' : ''} onClick={() => setViewMode('translated')}>Translation Only</button>
                </div>

                <div className="audio-toggle">
                    <audio id="player" ref={audioRef} autoPlay></audio>
                    <button
                        className={`icon-button ${audioEnabled ? 'audio-enabled' : 'audio-disabled'}`}
                        onClick={async () => {
                            try {
                                setAudioLoading(true);
                                if (audioEnabled) {
                                    await closeRtc();
                                    setAudioEnabled(false);
                                } else {
                                    // Ensure any user-gesture unlocking happens before creating the RTC connection
                                    await connectRtc();
                                    setAudioEnabled(true);
                                }
                            } catch (e) {
                                log('toggle audio failed', e);
                            } finally {
                                setAudioLoading(false);
                            }
                        }}
                        aria-pressed={audioEnabled}
                        aria-label={audioEnabled ? 'Disable audio' : 'Enable audio'}
                        aria-busy={audioLoading}
                        disabled={audioLoading}
                    >
                        <span className="mic-icon" aria-hidden>
                            {audioLoading ? (
                                <svg width="18" height="18" viewBox="0 0 50 50" aria-hidden>
                                    <circle cx="25" cy="25" r="20" fill="none" stroke="#666" strokeWidth="4" strokeLinecap="round" strokeDasharray="31.4 31.4" transform="rotate(-90 25 25)">
                                        <animateTransform attributeName="transform" type="rotate" from="0 25 25" to="360 25 25" dur="1s" repeatCount="indefinite" />
                                    </circle>
                                </svg>
                            ) : (
                                audioEnabled ? (
                                    <svg width="17" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden>
                                        <path d="M12 14a3 3 0 0 0 3-3V7a3 3 0 0 0-6 0v4a3 3 0 0 0 3 3z" stroke="#1F5FBF" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                        <path d="M19 11a7 7 0 0 1-14 0" stroke="#1F5FBF" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                        <path d="M12 18v3" stroke="#1F5FBF" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                        <path d="M9 21h6" stroke="#1F5FBF" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                    </svg>
                                ) : (
                                    <svg width="17" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden>
                                        <path d="M12 14a3 3 0 0 0 3-3V7a3 3 0 0 0-6 0v4a3 3 0 0 0 3 3z" stroke="#666" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                        <path d="M19 11a7 7 0 0 1-14 0" stroke="#666" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                        <path d="M12 18v3" stroke="#666" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                        <path d="M9 21h6" stroke="#666" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                        <path d="M4 4L20 20" stroke="#B00020" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                                    </svg>
                                )
                            )}
                        </span>
                    </button>
                </div>
            </div>
            <div className="captions" ref={containerRef}>
                {(lines || []).map((l, i) => {
                    const rawText = l.text ?? '';
                    const speaker = l.speaker ?? '';
                    const original = rawText[l.sourceLang] ?? rawText.en ?? '';
                    const translated = rawText[targetLang] ?? rawText.en ?? '';

                    return (
                        <div
                            key={`${l.startMs}-${l.endMs}-${i}`}
                            data-start-ms={l.realStartMs}
                            data-speaker-id={l.speakerId}
                            className={l.isFinal ? 'caption-block final' : 'caption-block partial'}
                        >
                            <div className="caption-speaker">[{speaker}]</div>
                            {viewMode !== 'translated' && (
                                <div className="caption-line original">
                                    <span className="label">(orig)</span>
                                    <span className="text">{original}</span>
                                </div>
                            )}
                            {viewMode !== 'original' && (
                                <div className="caption-line translated">
                                    <span className="label">(tran)</span>
                                    <span className="text">{translated}</span>
                                </div>
                            )}
                        </div>
                    );
                })}
            </div>
        </div>
    );
};
