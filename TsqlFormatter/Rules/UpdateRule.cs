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

        sb.Append(RuleHelpers.EmitCteHeader(upd, engine, indent));
        sb.Append($"{tabs}update {RuleHelpers.EmitTableRef(upd.Table, engine, indent)}");
        // A comment on the update line stays on it; comments written on their own line before
        // SET keep their own lines. Neither may hide the SET behind it.
        if (upd.TargetComment != null) RuleHelpers.AppendTrailing(sb, upd.TargetComment);
        foreach (var c in upd.PreSetComments) sb.Append($"\n{tabs}{c}");
        sb.Append($"\n{tabs}set");

        for (int i = 0; i < upd.Assignments.Count; i++)
        {
            var a      = upd.Assignments[i];
            var target = RuleHelpers.EmitExpr(a.Target, engine, indent + 1);
            var value  = RuleHelpers.EmitExpr(a.Value,  engine, indent + 1);
            sb.Append($"\n{tabs}\t{target} = {value}");
            // The comma closes the assignment BEFORE its comment — inside the comment it would be
            // commented out, leaving two assignments with no separator between them.
            if (i < upd.Assignments.Count - 1) sb.Append(",");
            if (a.TrailingComment != null) RuleHelpers.AppendTrailing(sb, a.TrailingComment);
        }

        // FROM / JOINs
        foreach (var c in upd.PreFromComments)
            sb.Append($"\n{tabs}{c}");
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

        // Comments written between the FROM/JOIN block and WHERE keep their own lines.
        foreach (var c in upd.PreWhereComments)
            sb.Append($"\n{tabs}{c}");

        // WHERE — same flat layout as SELECT (rule where-or: no outer parens are added,
        // and/or start their lines, source paren groups are preserved by the parser).
        if (upd.WhereConditions.Count > 0)
        {
            sb.Append($"\n{tabs}where");
            sb.Append($"\n{RuleHelpers.EmitConditions(upd.WhereConditions, engine, indent + 1)}");
        }

        return sb.ToString();
    }

}

}
