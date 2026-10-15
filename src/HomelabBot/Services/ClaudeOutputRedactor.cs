using System.Text.RegularExpressions;

namespace HomelabBot.Services;

// Claude's output goes to Discord, not to a terminal. Secrets it read on the host must not.
// Whole blocks and lines go first, so a KEY=sk-... line is not half-redacted by a token pattern.
internal static class ClaudeOutputRedactor
{
    internal const string Redacted = "[redacted]";

    // Up to the matching END line, or to the end of a text that was cut inside the block.
    private static readonly Regex PrivateKeyBlock = new(@"-----BEGIN ([A-Z0-9 ]*)PRIVATE KEY-----.*?(?:-----END \1PRIVATE KEY-----|\z)", RegexOptions.Singleline | RegexOptions.Compiled);

    // NAME=value or NAME: value on a line of its own (env file, YAML, export, list item), where the secret
    // word is a whole part of the name. A password or secret loses the rest of the line, and so does any NAME=.
    private static readonly Regex SecretAssignmentLine = new(
        @"^(?<name>[ \t]*(?:export[ \t]+|-[ \t]+)?[""']?(?:\w+[_.\-])*" +
        @"(?:(?:PASSWORD|PASSWD|SECRET|CLIENTSECRET)(?:[_.\-]\w+)*[""']?[ \t]*[:=]|(?:TOKEN|KEY|APIKEY|ACCESSTOKEN|AUTHTOKEN)(?:[_.\-]\w+)*[""']?[ \t]*=)" +
        @"[ \t]*)\S.*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // KEY: value or TOKEN: value: only a one-token or quoted value, so prose like "Key: the disk is full" stays.
    private static readonly Regex KeyColonLine = new(
        @"^(?<name>[ \t]*(?:-[ \t]+)?[""']?(?:\w+[_.\-])*(?:TOKEN|KEY|APIKEY|ACCESSTOKEN|AUTHTOKEN)(?:[_.\-]\w+)*[""']?[ \t]*:[ \t]*)" +
        @"(?:""[^""\n]*""|'[^'\n]*'|\S+)[ \t]*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // NAME=value inside a line, e.g. a docker run -e argument. Upper case only: "primary_key=id" is no secret.
    private static readonly Regex SecretAssignmentInline = new(@"(?<name>\b[A-Z0-9_]*(?:PASSWORD|SECRET|TOKEN|KEY)[A-Z0-9_]*=)[^\s""'`]+", RegexOptions.Compiled);

    // Only after "Authorization:": "Basic configuration" in prose is no credential.
    private static readonly Regex AuthorizationValue = new(@"(?<scheme>\bAuthorization[""']?[ \t]*[:=][ \t]*(?:(?:Bearer|Basic|Token)[ \t]+)?)[^\s""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlCredentials = new(@"(?<scheme>\b[a-z][a-z0-9+.\-]*://)[^\s/:@]+:[^\s/@]+@", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // sk-... (Anthropic, OpenAI), GitHub tokens, Slack tokens, JWT, Discord bot tokens.
    private static readonly Regex TokenShape = new(
        @"\bsk-[A-Za-z0-9_\-]{16,}|\bgh[pousr]_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}|\bxox[abprse]-[A-Za-z0-9\-]{10,}" +
        @"|\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}|\b[A-Za-z0-9]{24,28}\.[A-Za-z0-9_\-]{6}\.[A-Za-z0-9_\-]{27,40}\b", RegexOptions.Compiled);

    public static string Redact(string text)
    {
        text = PrivateKeyBlock.Replace(text, Redacted);
        text = SecretAssignmentLine.Replace(text, m => m.Groups["name"].Value + Redacted);
        text = KeyColonLine.Replace(text, m => m.Groups["name"].Value + Redacted);
        text = SecretAssignmentInline.Replace(text, m => m.Groups["name"].Value + Redacted);
        text = AuthorizationValue.Replace(text, m => m.Groups["scheme"].Value + Redacted);
        text = UrlCredentials.Replace(text, m => m.Groups["scheme"].Value + Redacted + "@");
        return TokenShape.Replace(text, Redacted);
    }
}
