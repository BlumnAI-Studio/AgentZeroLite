using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Common.Mcp;

/// <summary>
/// How a JSON-RPC message is written for a server: plain UTF-8, with only what JSON itself
/// requires escaped (<c>"</c>, <c>\</c>, control characters). System.Text.Json's encoders turn
/// Korean into <c>액터</c> (the default) or at least every emoji into a surrogate-pair
/// escape (the relaxed one). Both are valid JSON, but measured against memorizer 1.4.6
/// (2026-10-14): the same "액터 모델" search with similarity 0 returned unrelated memories when
/// escaped and the actor documents when sent as text — "actor model" in English was right either
/// way. Every other MCP client sends raw UTF-8, so a server is only ever tested against that.
/// </summary>
public static class McpWire
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(JsonNode node) => Unescape(node.ToJsonString(Relaxed));

    /// <summary>
    /// Turn every <c>\uXXXX</c> escape that does not have to be one back into its character,
    /// joining surrogate pairs. Escapes JSON requires, and lone surrogates (which have no UTF-8
    /// form), are left as they are. Other escapes (<c>\\</c>, <c>\n</c>, <c>\"</c>) are copied untouched.
    /// </summary>
    internal static string Unescape(string json)
    {
        if (json.IndexOf("\\u", StringComparison.Ordinal) < 0) return json;
        var sb = new StringBuilder(json.Length);
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (c != '\\' || i + 1 >= json.Length)
            {
                sb.Append(c);
                continue;
            }
            var next = json[i + 1];
            if (next != 'u' || !TryHex(json, i + 2, out var code))
            {
                sb.Append(c).Append(next); // \\ \" \n … — copy the pair, never look inside it
                i++;
                continue;
            }

            // A pair spans 12 chars: 🔍 (i … i + 11).
            if (char.IsHighSurrogate((char)code) && i + 11 < json.Length && json[i + 6] == '\\' && json[i + 7] == 'u'
                && TryHex(json, i + 8, out var low) && char.IsLowSurrogate((char)low))
            {
                sb.Append((char)code).Append((char)low);
                i += 11;
            }
            else if (code < 0x20 || code == '"' || code == '\\' || char.IsSurrogate((char)code))
            {
                sb.Append(json, i, 6);
                i += 5;
            }
            else
            {
                sb.Append((char)code);
                i += 5;
            }
        }
        return sb.ToString();
    }

    private static bool TryHex(string s, int start, out int value)
    {
        value = 0;
        return start + 4 <= s.Length
            && int.TryParse(s.AsSpan(start, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}
