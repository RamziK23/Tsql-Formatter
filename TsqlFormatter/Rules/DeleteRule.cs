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

        // True once a "from" has been written, so the joins of a DELETE FROM don't get a second one.
        bool fromWritten = false;
        if (del.Table != null)
        {
            // DELETE FROM table ... — normally one line. A comment written between DELETE and
            // FROM keeps its place instead: it closes the delete line and the clause moves down.
            if (del.PreFromComments.Count > 0)
            {
                sb.Append($"{tabs}delete{RuleHelpers.TrailingCommentSuffix(del.PreFromComments[0])}");
                foreach (var c in del.PreFromComments.Skip(1)) sb.Append($"\n{tabs}{c}");
                sb.Append($"\n{tabs}from {RuleHelpers.EmitTableRef(del.Table, engine, indent)}");
            }
            else
            {
                sb.Append($"{tabs}delete from {RuleHelpers.EmitTableRef(del.Table, engine, indent)}");
            }
            fromWritten = true;
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

        // A comment on the delete line stays on it — a -- comment offset by two tabs, a /* */
        // comment glued, exactly like a trailing comment anywhere else.
        if (del.TargetComment != null) sb.Append(RuleHelpers.TrailingCommentSuffix(del.TargetComment));
        if (!fromWritten)
            foreach (var c in del.PreFromComments) sb.Append($"\n{tabs}{c}");

        // FROM / JOINs
        foreach (var clause in del.FromClauses)
        {
            if (clause is JoinNode join)
                sb.Append(RuleHelpers.FormatJoin(join, engine, indent));
            else if (clause is TableRefNode tref)
            {
                if (!fromWritten) { sb.Append($"\n{tabs}from"); fromWritten = true; }
                sb.Append($" {RuleHelpers.EmitTableRef(tref, engine, indent)}");
            }
        }

        // Comments written between the FROM/JOIN block and WHERE keep their own lines.
        foreach (var c in del.PreWhereComments)
            sb.Append($"\n{tabs}{c}");

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
