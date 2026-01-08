namespace EchoBot.Translator
{
    public interface ITranslatorClient
    {
        Task<string> TranslateAsync(
            string text,
            string to,
            string category,
            CancellationToken ct = default);

        Task<Dictionary<string, string>> BatchTranslateAsync(
            string text,
            Dictionary<string, string> toCategory,
            CancellationToken ct = default);
    }
}
