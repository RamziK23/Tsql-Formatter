using System.Collections.Generic;
using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// DELETE FROM table WHERE ...
///   — or —
/// DELETE alias FROM table JOIN ... WHERE ...
/// </summary>
public sealed class DeleteRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is DeleteNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var del  = (DeleteNode)node;
        var tabs = RuleHelpers.Tabs(indent);
        var sb   = new System.Text.StringBuilder();

        if (del.Table != null)
        {
            // DELETE FROM table ...
            sb.Append($"{tabs}delete from {RuleHelpers.EmitTableRef(del.Table, engine, indent)}");
        }
        else if (del.TargetAlias != null)
        {
            // DELETE alias FROM ...
            sb.Append($"{tabs}delete {RuleHelpers.EmitTableRef(del.TargetAlias, engine, indent)}");
        }
        else
        {
            sb.Append($"{tabs}delete");
        }

        // FROM / JOINs
        if (del.FromClauses.Count > 0)
        {
            sb.Append($"\n{tabs}from");
            foreach (var clause in del.FromClauses)
            {
                if (clause is JoinNode join)
                    sb.Append(RuleHelpers.FormatJoin(join, engine, indent));
                else if (clause is TableRefNode tref)
                    sb.Append($" {RuleHelpers.EmitTableRef(tref, engine, indent)}");
            }
        }

        // WHERE — same flat layout as SELECT (rule where-or: no outer parens are added,
        // and/or start their lines, source paren groups are preserved by the parser).
        if (del.WhereConditions.Count > 0)
        {
            sb.Append($"\n{tabs}where");
            sb.Append($"\n{RuleHelpers.EmitConditions(del.WhereConditions, engine, indent + 1)}");
        }

        return sb.ToString();
    }

}

}
