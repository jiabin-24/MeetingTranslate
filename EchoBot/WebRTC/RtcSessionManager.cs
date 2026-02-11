using Azure;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Communication.Identity;
using Azure.Communication.Rooms;
using Azure.Core;
using EchoBot.Util;
using System.Collections.Concurrent;
using System.Security;

namespace EchoBot.WebRTC
{
    public class RtcSessionManager
    {
        private readonly CacheHelper _cache;

        private readonly CallAutomationClient _automationClient;

        private readonly ConcurrentDictionary<string, CallConnection> _callConnDic = new();

        private readonly IConfiguration _config;

        private readonly ILogger _logger;

        private readonly Uri _callback;

        private readonly string _acsConnectionString;

        public RtcSessionManager(CacheHelper cache, CallAutomationClient automationClient, IConfiguration config, ILogger<RtcSessionManager> logger)
        {
            _cache = cache;
            _automationClient = automationClient;
            _config = config;
            _logger = logger;

            _callback = new Uri($"{_config["AppBaseUrl"]}/api/acs/callback");
            _acsConnectionString = _config["ACSConnectionString"];
        }

        public async Task PlayText(string groupId, string text, string lang, string? voiceName)
        {
            var callConn = await EnsureGroupCallConnectionAsync(groupId);
            var media = callConn.GetCallMedia();
            voiceName ??= "zh-CN-XiaoxiaoNeural";

            var ssml = $"<speak version=\"1.0\" xml:lang=\"zh-CN\"><voice name=\"{SecurityElement.Escape(voiceName)}\">{SecurityElement.Escape(text)}</voice></speak>";
            var ssmlSrc = new SsmlSource(ssml);

            await media.PlayToAllAsync(ssmlSrc);
        }

        public async Task<CallConnection> EnsureGroupCallConnectionAsync(string groupId)
        {
            if (_callConnDic.TryGetValue(groupId, out CallConnection callConnection))
            {
                return callConnection;
            }

            var cachedConnectionId = await _cache.GetAsync<string>(ConnectKey(groupId));
            if (!string.IsNullOrEmpty(cachedConnectionId))
            {
                callConnection = _automationClient.GetCallConnection(cachedConnectionId);
                _callConnDic.TryAdd(groupId, callConnection);
                return callConnection;
            }

            var cachedRoomId = await _cache.GetAsync<string>(RoomKey(groupId));
            if (string.IsNullOrEmpty(cachedRoomId))
                throw new ArgumentException("Room has not init");

            var callLocator = new RoomCallLocator(cachedRoomId);
            var connectCallOptions = new ConnectCallOptions(callLocator, _callback)
            {
                CallIntelligenceOptions = new CallIntelligenceOptions() { CognitiveServicesEndpoint = new Uri(_config["CognitiveServicesEndpoint"]) },
            };

            ConnectCallResult callResult = await _automationClient.ConnectCallAsync(connectCallOptions);
            var callConnectionId = callResult.CallConnectionProperties.CallConnectionId;

            _logger.LogInformation($"CALL CONNECTION ID : {callConnectionId}");
            callConnection = GetConnection(callConnectionId);

            _callConnDic.TryAdd(groupId, callConnection);
            await _cache.GetOrSetAsync(ConnectKey(groupId), TimeSpan.FromHours(1), () => callConnectionId);

            return callConnection;
        }

        public async Task<Models.Room> AddParticipant(string groupId)
        {
            try
            {
                var cachedRoomId = await _cache.GetAsync<string>(RoomKey(groupId));
                if (string.IsNullOrEmpty(cachedRoomId))
                {
                    var initRoomResult = await InitRoom(groupId);
                    await _cache.SetAsync(RoomKey(groupId), TimeSpan.FromHours(1), initRoomResult.RoomId);
                    return initRoomResult;
                }

                var callConn = await EnsureGroupCallConnectionAsync(groupId);
                var (_, user) = await AddParticipant();

                var callInvite = new CallInvite(new CommunicationUserIdentifier(user.UserId));
                var addParticipantOptions = new AddParticipantOptions(callInvite)
                {
                    OperationContext = "addPSTNUserContext",
                    InvitationTimeoutInSeconds = 30,
                    OperationCallbackUri = _callback
                };

                await callConn.AddParticipantAsync(addParticipantOptions);

                return new Models.Room
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

        private async Task<Models.Room> InitRoom(string groupId)
        {
            var roomsClient = new RoomsClient(_acsConnectionString);
            var (participant, user) = await AddParticipant();
            var (participant2, user2) = await AddParticipant();
            var roomParticipants = new List<RoomParticipant> { participant, participant2 };

            var options = new CreateRoomOptions()
            {
                PstnDialOutEnabled = true,
                Participants = roomParticipants,
                ValidFrom = DateTime.UtcNow,
                ValidUntil = DateTime.UtcNow.AddMinutes(30)
            };

            var response = await roomsClient.CreateRoomAsync(options);
            var roomId = response.Value.Id;

            _logger.LogInformation($"ROOM ID: {response.Value.Id}");

            return new Models.Room
            {
                RoomId = roomId,
                Participants = [user]
            };
        }

        private async Task<(RoomParticipant, Models.RoomParticipant)> AddParticipant()
        {
            var identityClient = new CommunicationIdentityClient(_acsConnectionString);
            var scopes = new List<string> { "chat", "voip" };

            var user = identityClient.CreateUser();
            Response<AccessToken> userToken = await identityClient.GetTokenAsync(user.Value, scopes: scopes.Select(x => new CommunicationTokenScope(x)));
            var attendee = user.Value.RawId;
            var participant = new RoomParticipant(new CommunicationUserIdentifier(attendee))
            {
                Role = ParticipantRole.Presenter
            };

            return (participant, new Models.RoomParticipant
            {
                UserId = attendee,
                UserToken = userToken.Value.Token
            });
        }

        private CallConnection GetConnection(string callConnectionId)
        {
            CallConnection callConnection = !string.IsNullOrEmpty(callConnectionId) ? _automationClient.GetCallConnection(callConnectionId)
                : throw new ArgumentNullException("Call connection id is empty");
            return callConnection;
        }

        public async Task Dispose(string groupId)
        {
            await _cache.DeleteAsync(ConnectKey(groupId));
            await _cache.DeleteAsync(RoomKey(groupId));

            _callConnDic.Remove(groupId, out _);
        }

        private static string ConnectKey(string groupId) => $"CallConnectionId_{groupId}";

        private static string RoomKey(string groupId) => $"RoomId_{groupId}";
    }
}
