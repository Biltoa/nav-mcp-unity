using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Umcp.ToolGen;

internal static class Json
{
    /// <summary>
    /// Turn a C# literal expression back into its value. Done through the parser rather than by
    /// trimming quotes, because the thing this replaces used a regex and silently dropped every
    /// tool whose description contained a double quote or an apostrophe.
    /// </summary>
    public static string? Unquote(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return null;
        var parsed = SyntaxFactory.ParseExpression(expr);
        if (parsed is LiteralExpressionSyntax lit) return lit.Token.ValueText;
        if (parsed is InterpolatedStringExpressionSyntax) return null;
        return expr.Trim('"');
    }

    /// <summary>Serialise a string as a JSON string literal.</summary>
    public static string Str(string? s)
    {
        if (s is null) return "null";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Serialise a string as a C# string literal for generated source.</summary>
    public static string CsStr(string? s) => s is null ? "null" : Str(s);
}
