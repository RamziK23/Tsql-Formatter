using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

public sealed class GoSeparatorRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is GoSeparatorNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var go = (GoSeparatorNode)node;
        return go.Count != null ? $"GO {go.Count}" : "GO";
    }
}

/// <summary>A ';' written on a line of its own keeps that line.</summary>
public sealed class SemicolonRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is SemicolonNode;

    public string Format(AstNode node, FormatterEngine engine, int indent) =>
        $"{RuleHelpers.Tabs(indent)};";
}

}
