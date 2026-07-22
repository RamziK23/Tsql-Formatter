using System.Collections.Generic;
using System.Linq;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

public sealed class RawTokensRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is RawTokensNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var raw  = (RawTokensNode)node;
        var tabs = RuleHelpers.Tabs(indent);
        var meaningful = raw.Tokens
            .Where(t => t.Type is not (TokenType.Whitespace or TokenType.Newline))
            .ToList();

        if (meaningful.Count == 0) return string.Empty;

        // Each line comment on its own line; other tokens joined with smart spacing.
        var parts = new List<string>();
        var inline = new List<Token>();

        foreach (var t in meaningful)
        {
            if (t.Type is TokenType.LineComment or TokenType.BlockComment)
            {
                if (inline.Count > 0) { parts.Add(EmitInline(inline)); inline.Clear(); }
                parts.Add(t.Value);
            }
            else
            {
                inline.Add(t);
            }
        }
        if (inline.Count > 0) parts.Add(EmitInline(inline));

        return string.Join("\n", parts.Select(p => $"{tabs}{p}"));
    }

    /// <summary>
    /// Joins a run of raw tokens with smart spacing (no space around '(' ')' ',' '.',
    /// one space after ','), lowercasing keywords and function names (identifier before '(').
    /// </summary>
    private static string EmitInline(List<Token> tokens)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            bool isFunction = t.Type == TokenType.Identifier
                && i + 1 < tokens.Count && tokens[i + 1].Type == TokenType.LeftParen;
            string val = t.Type == TokenType.Keyword || isFunction ? t.Value.ToLowerInvariant() : t.Value;

            if (i > 0)
            {
                var prev = tokens[i - 1];
                bool noSpaceBefore = t.Type is TokenType.LeftParen or TokenType.RightParen
                                            or TokenType.Comma or TokenType.Dot;
                if (prev.Type == TokenType.Comma)                              sb.Append(' ');  // "a, b"
                else if (noSpaceBefore)                                        { }               // before ( ) , .
                else if (prev.Type is TokenType.LeftParen or TokenType.Dot)    { }               // after ( or .
                else                                                           sb.Append(' ');   // normal gap
            }
            sb.Append(val);
        }
        return sb.ToString();
    }
}

}
