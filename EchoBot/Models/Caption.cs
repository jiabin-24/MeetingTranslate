namespace EchoBot.Models
{
    public class Caption
    {
        // 客户端订阅消息（首条消息发送）
        public record SubscribeMessage(string Type, string MeetingId, string TargetLang);

        // 服务端推送给前端的字幕负载
        public record CaptionPayload(
            string Type,          // "caption"
            string MeetingId,
            string? Speaker,
            string Lang,          // 源语言，如 "en" / "zh"
            string? TargetLang,   // 目标语言（可选，服务端/客户端都可据此过滤）
            string Text,
            bool IsFinal,         // partial / final
            long? StartMs,
            long? EndMs
        );

        // 首条鉴权消息（示例）
        public record AuthMessage(string Type, string Token);
    }
}
