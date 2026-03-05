using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Communication.Identity;
using Azure.Communication.Rooms;
using EchoBot.Constants;
using EchoBot.Models;
using EchoBot.Util;
using System.Collections.Concurrent;

namespace EchoBot.WebRTC
{
    public class RtcSessionManager(CacheHelper cache, CallAutomationClient automationClient, IConfiguration config, ILogger<RtcSessionManager> logger)
    {
        private readonly CacheHelper _cache = cache;

        private readonly CallAutomationClient _automationClient = automationClient;

        private readonly ConcurrentDictionary<string, CallConnection> _callConnDic = new();

        private readonly ILogger _logger = logger;

        private readonly Uri _callback = new($"{config["AppBaseUrl"]}/api/acs/callback");

        private readonly Uri _mediaWebSocketUri = new($"{config["AppBaseUrl"].Replace("https", "wss")}/ws/media");

        private readonly string _acsConnectionString = config["ACSConnectionString"];

        public async Task<(string?, CallConnection)> EnsureGroupCallConnectionAsync(string groupId)
        {
            if (_callConnDic.TryGetValue(groupId, out CallConnection callConnection))
                return (null, callConnection);

            string cachedConnectionId;
            if (!string.IsNullOrEmpty(cachedConnectionId = await _cache.GetAsync<string>(CacheConstants.AcsConnectKey(groupId))))
            {
                callConnection = _automationClient.GetCallConnection(cachedConnectionId);
                _callConnDic.TryAdd(groupId, callConnection);
                return (null, callConnection);
            }

            string cachedRoomId;
            if (string.IsNullOrEmpty(cachedRoomId = await _cache.GetAsync<string>(CacheConstants.AcsRoomKey(groupId))))
                return ("Room has not been inited", null);

            var callLocator = new RoomCallLocator(cachedRoomId);
            // Media streaming configuration
            var mediaStreamingOptions = new MediaStreamingOptions(MediaStreamingAudioChannel.Mixed, StreamingTransport.Websocket)
            {
                TransportUri = _mediaWebSocketUri,
                StartMediaStreaming = true,
                EnableBidirectional = true,
                EnableDtmfTones = true,
                AudioFormat = AudioFormat.Pcm16KMono
            };

            var connectCallOptions = new ConnectCallOptions(callLocator, _callback)
            {
                MediaStreamingOptions = mediaStreamingOptions
            };

            ConnectCallResult callResult = await _automationClient.ConnectCallAsync(connectCallOptions);
            var callConnectionId = callResult.CallConnectionProperties.CallConnectionId;

            _logger.LogInformation("CALL Azure Communication Service CONNECTION ID : {callConnectionId}", callConnectionId);
            callConnection = _automationClient.GetCallConnection(callConnectionId);

            _callConnDic.TryAdd(groupId, callConnection);
            await _cache.SetAsync(CacheConstants.AcsConnectKey(groupId), TimeSpan.FromHours(2), callConnectionId);

            return (null, callConnection);
        }

        public async Task<Room> AddRoomParticipant(AddRoomParticipant addPar)
        {
            try
            {
                var cachedRoomId = await _cache.GetAsync<string>(CacheConstants.AcsRoomKey(addPar.GroupId));
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

            await _cache.SetAsync(CacheConstants.AcsRoomKey(groupId), TimeSpan.FromHours(2), roomId);
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

            var participants = (await _cache.GetAsync<List<Models.RoomParticipant>>(CacheConstants.AcsRoomParticipantKey(groupId)) ?? []).Union([roomParticipant]);
            await _cache.SetAsync(CacheConstants.AcsRoomParticipantKey(groupId), TimeSpan.FromHours(2), participants);

            return (participant, roomParticipant);
        }

        public async Task Dispose(string groupId)
        {
            await _cache.DeleteAsync(CacheConstants.AcsConnectKey(groupId));
            await _cache.DeleteAsync(CacheConstants.AcsRoomKey(groupId));
            await _cache.DeleteAsync(CacheConstants.AcsRoomParticipantKey(groupId));

            _callConnDic.Remove(groupId, out _);
        }
    }
}
