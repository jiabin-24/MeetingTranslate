using System.Text;

public enum LangGuess { Chinese, English, Mixed, Unknown }

public static class LangDetect
{
    public static LangGuess DetectZhEn(string text, double threshold = 0.6)
    {
        if (string.IsNullOrWhiteSpace(text)) return LangGuess.Unknown;

        int zh = 0, en = 0, letterLike = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            // 忽略空白/标点/数字
            if (Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune) || Rune.IsDigit(rune))
                continue;

            if (IsCjk(rune))
            {
                zh++;
                letterLike++;
            }
            else if (IsLatinLetter(rune))
            {
                en++;
                letterLike++;
            }
            else
            {
                // 其他脚本（日文假名、韩文等）也算“字母类”，避免误判
                if (Rune.IsLetter(rune)) letterLike++;
            }
        }

        if (letterLike == 0) return LangGuess.Unknown; // 只有符号数字等

        double zhRatio = (double)zh / Math.Max(1, zh + en);
        double enRatio = (double)en / Math.Max(1, zh + en);

        // 如果既有中文又有英文，且都很明显 => Mixed
        if (zh > 0 && en > 0 && zhRatio < threshold && enRatio < threshold)
            return LangGuess.Mixed;

        if (zhRatio >= threshold) return LangGuess.Chinese;
        if (enRatio >= threshold) return LangGuess.English;

        // 很短或信息不足
        if (zh > en) return LangGuess.Chinese;
        if (en > zh) return LangGuess.English;
        return LangGuess.Unknown;
    }

    private static bool IsLatinLetter(Rune r)
        => (r.Value >= 'A' && r.Value <= 'Z') || (r.Value >= 'a' && r.Value <= 'z');

    // CJK Unified Ideographs + Extension A（常见中文）
    private static bool IsCjk(Rune r)
        => (r.Value >= 0x4E00 && r.Value <= 0x9FFF)   // CJK Unified Ideographs
        || (r.Value >= 0x3400 && r.Value <= 0x4DBF);  // Extension A
}
