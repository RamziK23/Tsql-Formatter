using System.Collections.Generic;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// Formats UNION / UNION ALL / EXCEPT / INTERSECT chains.
/// Each member SELECT is emitted at the same indent level, separated by the operator keyword on its own line.
/// </summary>
public sealed class SetOperationRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is SetOperationNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var parts = new List<string>();
        Flatten(node, engine, indent, parts);
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Recursively flattens a left-leaning SetOperationNode tree into ordered parts:
    /// [left_sql, "OPERATOR", right_sql].
    /// </summary>
    private void Flatten(AstNode node, FormatterEngine engine, int indent, List<string> parts)
    {
        if (node is SetOperationNode set)
        {
            Flatten(set.Left, engine, indent, parts);
            // A blank line precedes and follows the set operator keyword.
            parts.Add("");
            parts.Add(RuleHelpers.Tabs(indent) + set.Operator.ToLowerInvariant());
            parts.Add("");
            parts.Add(engine.Format(set.Right, indent));
        }
        else
        {
            parts.Add(engine.Format(node, indent));
        }
    }
}

}
