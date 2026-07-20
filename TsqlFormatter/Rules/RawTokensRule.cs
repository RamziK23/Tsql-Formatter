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

        // Each line comment on its own line; other tokens joined by space.
        var parts = new List<string>();
        var inline = new List<string>();

        foreach (var t in meaningful)
        {
            if (t.Type is TokenType.LineComment or TokenType.BlockComment)
            {
                if (inline.Count > 0) { parts.Add(string.Join(" ", inline)); inline.Clear(); }
                parts.Add(t.Value);
            }
            else
            {
                inline.Add(t.Value);
            }
        }
        if (inline.Count > 0) parts.Add(string.Join(" ", inline));

        return string.Join("\n", parts.Select(p => $"{tabs}{p}"));
    }
}

}
