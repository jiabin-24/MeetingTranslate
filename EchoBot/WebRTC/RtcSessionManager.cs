using Azure.AI.Agents.Persistent;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Communication.Identity;
using Azure.Communication.Rooms;
using EchoBot.Constants;
using EchoBot.Models;
using EchoBot.Util;

namespace EchoBot.WebRTC
{
    public class RtcSessionManager
    {
        private readonly CacheHelper _cache;

        private readonly CallAutomationClient _automationClient;

        private readonly ILogger _logger;

        private readonly string _key;

        private readonly Uri _callback;

        private readonly string _acsConnectionString;

        private readonly Uri _mediaWebSocketUri;

        private CallConnection? _callConn;

        public string ThreadId { get; set; }

        public string TargetLang { get; set; }

        public RtcSessionManager(string threadId, string targetLang)
        {
            _cache = ServiceLocator.GetRequiredService<CacheHelper>();
            _automationClient = ServiceLocator.GetRequiredService<CallAutomationClient>();
            _logger = ServiceLocator.GetRequiredService<ILogger<RtcSessionManager>>();
            _key = $"{threadId}:{targetLang}";
            
            var config = ServiceLocator.GetRequiredService<IConfiguration>();
            _callback = new($"{config["AppBaseUrl"]}/api/acs/callback");
            _acsConnectionString = config["ACSConnectionString"];
            _mediaWebSocketUri = new($"{config["AppBaseUrl"].Replace("https", "wss")}/ws/media?threadId={threadId}&targetLang={targetLang}");

            ThreadId = threadId;
            TargetLang = targetLang;
        }

        private async Task<(string?, CallConnection)> EnsureGroupCallConnectionAsync()
        {
            if (_callConn != null)
                return (null, _callConn);

            string cachedConnId;
            if (!string.IsNullOrEmpty(cachedConnId = await _cache.GetAsync<string>(CacheConstants.AcsConnectKey(_key))))
            {
                _callConn = _automationClient.GetCallConnection(cachedConnId);
                return (null, _callConn);
            }

            string cachedRoomId;
            if (string.IsNullOrEmpty(cachedRoomId = await _cache.GetAsync<string>(CacheConstants.AcsRoomKey(_key))))
                return ("Room has not been inited", null);

            var callLocator = new RoomCallLocator(cachedRoomId);
            // Media streaming configuration
            var mediaStreamingOptions = new MediaStreamingOptions(MediaStreamingAudioChannel.Mixed, StreamingTransport.Websocket)
            {
                TransportUri = _mediaWebSocketUri,
                StartMediaStreaming = true,
                EnableBidirectional = true,
                EnableDtmfTones = true,
                AudioFormat = AudioFormat.Pcm16KMono,
            };

            var connectCallOptions = new ConnectCallOptions(callLocator, _callback)
            {
                MediaStreamingOptions = mediaStreamingOptions
            };

            _logger.LogInformation("ConnectCallAsync start. roomId={roomId}, callback={callback}, mediaUri={mediaUri}", cachedRoomId, _callback, _mediaWebSocketUri);

            ConnectCallResult callResult = await _automationClient.ConnectCallAsync(connectCallOptions);
            cachedConnId = callResult.CallConnectionProperties.CallConnectionId;

            _logger.LogInformation("CALL Azure Communication Service CONNECTION ID : {callConnectionId}", cachedConnId);
            _callConn = _automationClient.GetCallConnection(cachedConnId);

            await _cache.SetAsync(CacheConstants.AcsConnectKey(_key), TimeSpan.FromHours(2), cachedConnId);

            return (null, _callConn);
        }

        public static async Task<bool> EnsureGroupCallConnectionAsync(RtcSessionManager rtcSession)
        {
            if (rtcSession == null) return false;

            var (msg, _) = await rtcSession.EnsureGroupCallConnectionAsync();
            return string.IsNullOrEmpty(msg);
        }

        public async Task<Room> AddRoomParticipant(AddRoomParticipant addPar)
        {
            try
            {
                var cachedRoomId = await _cache.GetAsync<string>(CacheConstants.AcsRoomKey(_key));
                if (string.IsNullOrEmpty(cachedRoomId))
                {
                    // 如果房间不存在，先创建房间（同时添加参与者），然后返回
                    return await InitRoom(addPar.GroupId, addPar.Lang, addPar.SourceLang, addPar.ParticipantId);
                }

                // 房间已存在，直接添加参与者并返回
                var (participant, user) = await CreateParticipant(addPar.GroupId, addPar.Lang, addPar.SourceLang, addPar.ParticipantId);
                var roomsClient = new RoomsClient(_acsConnectionString);
                await roomsClient.AddOrUpdateParticipantsAsync(cachedRoomId, [participant]);

                return new Room
                {
                    RoomId = cachedRoomId,
                    Participants = [user]
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Add participant failed");
                return null;
            }
        }

        private async Task<Room> InitRoom(string groupId, string lang, string sourceLang, string participantId)
        {
            var roomsClient = new RoomsClient(_acsConnectionString);
            var (participant, user) = await CreateParticipant(groupId, lang, sourceLang, participantId);
            var roomParticipants = new List<Azure.Communication.Rooms.RoomParticipant> { participant };

            var options = new CreateRoomOptions()
            {
                PstnDialOutEnabled = true,
                Participants = roomParticipants,
                ValidFrom = DateTime.UtcNow,
                ValidUntil = DateTime.UtcNow.AddMinutes(30)
            };

            var response = await roomsClient.CreateRoomAsync(options);
            var roomId = response.Value.Id;

            await _cache.SetAsync(CacheConstants.AcsRoomKey(_key), TimeSpan.FromHours(2), roomId);
            _logger.LogInformation("Init with ROOM ID: {RoomId}", roomId);

            return new Models.Room
            {
                RoomId = roomId,
                Participants = [user]
            };
        }

        private async Task<(Azure.Communication.Rooms.RoomParticipant, Models.RoomParticipant)> CreateParticipant(string groupId, string lang, string sourceLang, string participantId)
        {
            var identityClient = new CommunicationIdentityClient(_acsConnectionString);
            var scopes = new List<string> { "chat", "voip" };

            var user = identityClient.CreateUser().Value;
            var userId = user.RawId;
            var userToken = (await identityClient.GetTokenAsync(user, scopes: scopes.Select(x => new CommunicationTokenScope(x)))).Value;
            var participant = new Azure.Communication.Rooms.RoomParticipant(new CommunicationUserIdentifier(userId))
            {
                Role = ParticipantRole.Attendee
            };

            var roomParticipant = new Models.RoomParticipant
            {
                UserId = userId,
                EntraId = participantId,
                UserToken = userToken.Token,
                Lang = lang,
                SourceLang = sourceLang,
            };

            var participants = (await _cache.GetAsync<List<Models.RoomParticipant>>(CacheConstants.AcsRoomParticipantKey(_key)) ?? []).Union([roomParticipant]);
            await _cache.SetAsync(CacheConstants.AcsRoomParticipantKey(_key), TimeSpan.FromHours(2), participants);

            return (participant, roomParticipant);
        }

        public async Task Dispose()
        {
            await _cache.DeleteAsync(CacheConstants.AcsConnectKey(_key));
            await _cache.DeleteAsync(CacheConstants.AcsRoomKey(_key));
            await _cache.DeleteAsync(CacheConstants.AcsRoomParticipantKey(_key));

            _callConn = null;
        }
    }
}
