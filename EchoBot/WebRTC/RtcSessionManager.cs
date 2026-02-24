using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Communication.Identity;
using Azure.Communication.Rooms;
using EchoBot.Util;
using System.Collections.Concurrent;
using System.Security;

namespace EchoBot.WebRTC
{
    public class RtcSessionManager(CacheHelper cache, CallAutomationClient automationClient, IConfiguration config, ILogger<RtcSessionManager> logger)
    {
        private readonly CacheHelper _cache = cache;

        private readonly CallAutomationClient _automationClient = automationClient;

        private readonly ConcurrentDictionary<string, CallConnection> _callConnDic = new();

        private readonly ILogger _logger = logger;

        private readonly Uri _callback = new($"{config["AppBaseUrl"]}/api/acs/callback");

        private readonly Uri _cognitiveServicesEndpoint = new(config["CognitiveServicesEndpoint"]);

        private readonly string _acsConnectionString = config["ACSConnectionString"];

        public async Task PlayText(string groupId, string text, string lang, string speakerId)
        {
            try
            {
                var callConn = await EnsureGroupCallConnectionAsync(groupId);
                var media = callConn.GetCallMedia();
                var acsParticipants = (await callConn.GetParticipantsAsync()).Value;
                var targetParticipants = (await _cache.GetAsync<List<Models.RoomParticipant>>(RoomParticipantKey(groupId)))
                    .Where(p => p.Lang.Equals(lang) && !p.EntraId.Equals(speakerId)).Select(p => p.UserId).ToList();

                var targets = acsParticipants.Where(p => p.Identifier is CommunicationUserIdentifier && targetParticipants.Contains(p.Identifier.RawId))
                    .Select(p => p.Identifier).ToList();

                if (targets.Count == 0)
                    return;

                (string locale, string voice) = lang?.ToLowerInvariant() switch
                {
                    var l when l.StartsWith("zh") => ("zh-CN", "zh-CN-XiaoxiaoNeural"),
                    var l when l.StartsWith("ja") => ("ja-JP", "ja-JP-NanamiNeural"),
                    var l when l.StartsWith("ko") => ("ko-KR", "ko-KR-SunHiNeural"),
                    var l when l.StartsWith("en") => ("en-US", "en-US-JennyNeural"),
                    _ => ("en-US", "en-US-JennyNeural")
                };

                var ssml = $"<speak version=\"1.0\" xml:lang=\"{locale}\"><voice name=\"{voice}\">{SecurityElement.Escape(text)}</voice></speak>";
                var ssmlSrc = new SsmlSource(ssml);

                foreach (var target in targets)
                {
                    await media.PlayAsync(ssmlSrc, [target]);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PlayText to ACS failed");
            }
        }

        public async Task<CallConnection> EnsureGroupCallConnectionAsync(string groupId)
        {
            if (_callConnDic.TryGetValue(groupId, out CallConnection callConnection))
                return callConnection;

            string cachedConnectionId;
            if (!string.IsNullOrEmpty(cachedConnectionId = await _cache.GetAsync<string>(ConnectKey(groupId))))
            {
                callConnection = _automationClient.GetCallConnection(cachedConnectionId);
                _callConnDic.TryAdd(groupId, callConnection);
                return callConnection;
            }

            string cachedRoomId;
            if (string.IsNullOrEmpty(cachedRoomId = await _cache.GetAsync<string>(RoomKey(groupId))))
                throw new ArgumentException("Room has not been inited");

            var callLocator = new RoomCallLocator(cachedRoomId);
            var connectCallOptions = new ConnectCallOptions(callLocator, _callback)
            {
                CallIntelligenceOptions = new CallIntelligenceOptions() { CognitiveServicesEndpoint = _cognitiveServicesEndpoint },
            };

            ConnectCallResult callResult = await _automationClient.ConnectCallAsync(connectCallOptions);
            var callConnectionId = callResult.CallConnectionProperties.CallConnectionId;

            _logger.LogInformation("CALL Azure Communication Service CONNECTION ID : {callConnectionId}", callConnectionId);
            callConnection = _automationClient.GetCallConnection(callConnectionId);

            _callConnDic.TryAdd(groupId, callConnection);
            await _cache.SetAsync(ConnectKey(groupId), TimeSpan.FromHours(2), callConnectionId);

            return callConnection;
        }

        public async Task<Models.Room> AddRoomParticipant(string groupId, string lang, string participantId)
        {
            try
            {
                var cachedRoomId = await _cache.GetAsync<string>(RoomKey(groupId));
                if (string.IsNullOrEmpty(cachedRoomId))
                {
                    // 如果房间不存在，先创建房间（同时添加参与者），然后返回
                    return await InitRoom(groupId, lang, participantId);
                }

                // 房间已存在，直接添加参与者并返回
                var (participant, user) = await CreateParticipant(groupId, lang, participantId);
                var roomsClient = new RoomsClient(_acsConnectionString);
                await roomsClient.AddOrUpdateParticipantsAsync(cachedRoomId, [participant]);

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

        private async Task<Models.Room> InitRoom(string groupId, string lang, string participantId)
        {
            var roomsClient = new RoomsClient(_acsConnectionString);
            var (participant, user) = await CreateParticipant(groupId, lang, participantId);
            var roomParticipants = new List<RoomParticipant> { participant };

            var options = new CreateRoomOptions()
            {
                PstnDialOutEnabled = true,
                Participants = roomParticipants,
                ValidFrom = DateTime.UtcNow,
                ValidUntil = DateTime.UtcNow.AddMinutes(30)
            };

            var response = await roomsClient.CreateRoomAsync(options);
            var roomId = response.Value.Id;

            await _cache.SetAsync(RoomKey(groupId), TimeSpan.FromHours(2), roomId);
            _logger.LogInformation("Init with ROOM ID: {RoomId}", roomId);

            return new Models.Room
            {
                RoomId = roomId,
                Participants = [user]
            };
        }

        private async Task<(RoomParticipant, Models.RoomParticipant)> CreateParticipant(string groupId, string lang, string participantId)
        {
            var identityClient = new CommunicationIdentityClient(_acsConnectionString);
            var scopes = new List<string> { "chat", "voip" };

            var user = identityClient.CreateUser().Value;
            var userId = user.RawId;
            var userToken = (await identityClient.GetTokenAsync(user, scopes: scopes.Select(x => new CommunicationTokenScope(x)))).Value;
            var participant = new RoomParticipant(new CommunicationUserIdentifier(userId))
            {
                Role = ParticipantRole.Attendee
            };

            var roomParticipant = new Models.RoomParticipant
            {
                UserId = userId,
                EntraId = participantId,
                UserToken = userToken.Token,
                Lang = lang
            };

            var participants = (await _cache.GetAsync<List<Models.RoomParticipant>>(RoomParticipantKey(groupId)) ?? []).Union([roomParticipant]);
            await _cache.SetAsync(RoomParticipantKey(groupId), TimeSpan.FromHours(2), participants);

            return (participant, roomParticipant);
        }

        public async Task Dispose(string groupId)
        {
            await _cache.DeleteAsync(ConnectKey(groupId));
            await _cache.DeleteAsync(RoomKey(groupId));
            await _cache.DeleteAsync(RoomParticipantKey(groupId));

            _callConnDic.Remove(groupId, out _);
        }

        private static string ConnectKey(string groupId) => $"CallConnectionId_{groupId}";

        private static string RoomKey(string groupId) => $"RoomId_{groupId}";

        private static string RoomParticipantKey(string groupId) => $"RoomParticipantId_{groupId}";
    }
}
