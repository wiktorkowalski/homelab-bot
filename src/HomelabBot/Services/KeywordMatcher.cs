namespace HomelabBot.Services;

public static class KeywordMatcher
{
    public static List<string> Tokenize(string text, int minLength = 3)
    {
        return text.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(k => k.Length >= minLength)
            .Distinct()
            .ToList();
    }

    public static double Score(string query, string target, int minKeywordLength = 3)
    {
        var keywords = Tokenize(query, minKeywordLength);
        if (keywords.Count == 0)
        {
            return 0;
        }

        var targetLower = target.ToLowerInvariant();
        var targetWords = targetLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var score = 0.0;
        foreach (var keyword in keywords)
        {
            if (targetWords.Contains(keyword))
            {
                score += 2;
            }
            else if (targetLower.Contains(keyword))
            {
                score += 1;
            }
        }

        return score;
    }

    public static int CountMatches(string[] keywords, string target)
    {
        var targetLower = target.ToLowerInvariant();
        return keywords.Count(k => targetLower.Contains(k));
    }
}
