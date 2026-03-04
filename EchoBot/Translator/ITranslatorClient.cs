namespace EchoBot.Translator
{
    public interface ITranslatorClient
    {
        Task<Dictionary<string, string>> BatchTranslateAsync(
            string text,
            string sourceLang,
            Dictionary<string, string> toCategory,
            CancellationToken ct = default);
    }
}
