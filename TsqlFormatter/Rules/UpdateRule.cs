using System.Collections.Generic;
using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// UPDATE table
/// SET
/// \tcol1 = val1,
/// \tcol2 = val2
/// FROM ...
/// WHERE ...
/// </summary>
public sealed class UpdateRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is UpdateNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var upd  = (UpdateNode)node;
        var tabs = RuleHelpers.Tabs(indent);
        var sb   = new System.Text.StringBuilder();

        sb.Append($"{tabs}update {RuleHelpers.EmitTableRef(upd.Table, engine, indent)}\n");
        sb.Append($"{tabs}set");

        for (int i = 0; i < upd.Assignments.Count; i++)
        {
            var a      = upd.Assignments[i];
            var target = RuleHelpers.EmitExpr(a.Target, engine, indent + 1);
            var value  = RuleHelpers.EmitExpr(a.Value,  engine, indent + 1);
            sb.Append($"\n{tabs}\t{target} = {value}");
            if (i < upd.Assignments.Count - 1) sb.Append(",");
        }

        // FROM / JOINs
        if (upd.FromClauses.Count > 0)
        {
            sb.Append($"\n{tabs}from");
            foreach (var clause in upd.FromClauses)
            {
                if (clause is JoinNode join)
                    sb.Append(RuleHelpers.FormatJoin(join, engine, indent));
                else if (clause is TableRefNode tref)
                    sb.Append($" {RuleHelpers.EmitTableRef(tref, engine, indent)}");
            }
        }

        // WHERE (rule 2.6: OR wrapped in brackets)
        if (upd.WhereConditions.Count > 0)
        {
            if (RuleHelpers.HasOrConditions(upd.WhereConditions))
            {
                sb.Append($"\n{tabs}where\n");
                sb.Append(RuleHelpers.EmitOrGroupConditions(upd.WhereConditions, engine, indent));
            }
            else
            {
                // Every condition on its own indented line, even a single one.
                sb.Append($"\n{tabs}where");
                sb.Append($"\n{RuleHelpers.EmitConditions(upd.WhereConditions, engine, indent + 1)}");
            }
        }

        return sb.ToString();
    }

}

}
