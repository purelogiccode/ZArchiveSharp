using System.Text;

namespace ZArchiveSharp.Cli;

/// <summary>
/// Supplies the PureLogicCode API key shared by the bug-report and usage
/// senders. The key is stored double-obfuscated (two XOR passes with a
/// reversal between them, then Base64) so no plaintext credential appears in
/// the shipped binary or a casual strings dump; it is decoded once when this
/// type is first touched. This is obfuscation, not security: a determined
/// attacker with the binary can still recover the key. CLI binary project
/// only.
/// </summary>
internal static class ApiKeyProvider
{
    private const string EncodedKey =
        "b2BzEQkrIW9mB2FgQWdXb25kMBpwBXN0ZVo1ORl1BToNcSA0LEMzY2txDi8Gfks3UnhgNz4rIyVFbh9qX3p+W0c=";

    private const string Pad1 = "Zar#Cli#2026#A7!";
    private const string Pad2 = "PureLogicCode#B9@send";

    /// <summary>The decoded API key, materialized on first use.</summary>
    internal static string ApiKey { get; } = Decode();

    private static string Decode()
    {
        var data = Convert.FromBase64String(EncodedKey);
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= (byte)Pad2[i % Pad2.Length];
        }

        Array.Reverse(data);
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= (byte)Pad1[i % Pad1.Length];
        }

        return Encoding.UTF8.GetString(data);
    }
}
