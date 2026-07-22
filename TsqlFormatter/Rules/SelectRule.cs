using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

public sealed class SelectRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is SelectStatementNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var sel = (SelectStatementNode)node;
        var sb  = new System.Text.StringBuilder();
        var tabs = RuleHelpers.Tabs(indent);

        // ── CTEs ──────────────────────────────────────────────────────────────
        if (sel.CteDefinitions.Count > 0)
        {
            sb.Append($"{tabs}with ");
            for (int i = 0; i < sel.CteDefinitions.Count; i++)
            {
                var cte = (CteDefinitionNode)sel.CteDefinitions[i];
                sb.Append($"{cte.Name.Value} as (\n");
                sb.Append(engine.Format(cte.Body, indent + 1));
                sb.Append($"\n{tabs})");
                if (i < sel.CteDefinitions.Count - 1) sb.Append($",\n{tabs}");
            }
            sb.Append("\n");
        }

        // ── SELECT header ─────────────────────────────────────────────────────
        var header = new System.Text.StringBuilder($"{tabs}select");
        if (sel.IsDistinct) header.Append(" distinct");
        if (sel.TopExpr != null) header.Append($" top {sel.TopExpr}");

        // ── Columns ───────────────────────────────────────────────────────────
        if (sel.Columns.Count == 1)
        {
            var col = sel.Columns[0];
            var colStr = RuleHelpers.EmitExpr(col.Expression, engine, indent);
            var alias  = col.Alias != null ? $" as {col.Alias.Value}" : "";
            var comment = col.TrailingComment != null ? $"{RuleHelpers.Tabs(2)}{col.TrailingComment}" : "";
            var leading = col.LeadingComment != null ? $"{col.LeadingComment} " : "";
            sb.Append($"{header} {leading}{colStr}{alias}{comment}");
        }
        else
        {
            sb.Append(header);
            for (int i = 0; i < sel.Columns.Count; i++)
            {
                var col     = sel.Columns[i];
                var colStr  = RuleHelpers.EmitExpr(col.Expression, engine, indent + 1);
                var alias   = col.Alias != null ? $" as {col.Alias.Value}" : "";
                var comma   = i < sel.Columns.Count - 1 ? "," : "";
                // Rule 4.1.2: comma goes BEFORE the trailing comment, comment offset by 2 tabs
                var comment = col.TrailingComment != null ? $"{RuleHelpers.Tabs(2)}{col.TrailingComment}" : "";
                // Leading block comment sits before the column expression
                var leading = col.LeadingComment != null ? $"{col.LeadingComment} " : "";
                sb.Append($"\n{tabs}\t{leading}{colStr}{alias}{comma}{comment}");
            }
        }

        // ── FROM ──────────────────────────────────────────────────────────────
        if (sel.FromClauses.Count > 0)
        {
            sb.Append($"\n{tabs}from");
            foreach (var clause in sel.FromClauses)
            {
                if (clause is JoinNode join)
                    sb.Append(RuleHelpers.FormatJoin(join, engine, indent));
                else if (clause is TableRefNode tref)
                {
                    sb.Append($" {RuleHelpers.EmitTableRef(tref, engine, indent)}");
                    // A same-line -- comment stays on the FROM line.
                    if (tref.TrailingComment != null)
                        sb.Append($"{RuleHelpers.Tabs(2)}{tref.TrailingComment}");
                }
            }
        }

        // Standalone comments that trailed the FROM/JOIN block (e.g. before WHERE).
        foreach (var c in sel.PostFromComments)
            sb.Append($"\n{tabs}{c}");

        // ── WHERE ─────────────────────────────────────────────────────────────
        var whereConds = sel.WhereConditions.Where(c => c is not ConditionNode cn || cn.LogicalOp != "having").ToList();
        var havingCond = sel.WhereConditions.OfType<ConditionNode>().FirstOrDefault(cn => cn.LogicalOp == "having");

        if (whereConds.Count > 0)
        {
            sb.Append($"\n{tabs}where");
            if (whereConds.Count == 1 && whereConds[0] is ConditionNode sc && sc.LogicalOp == null
                && sc.Expression is not ConditionGroupNode)
            {
                var cmt = sc.TrailingComment != null ? $" {sc.TrailingComment}" : "";
                sb.Append($" {RuleHelpers.EmitExpr(sc.Expression, engine, indent)}{cmt}");
            }
            else
                sb.Append($"\n{RuleHelpers.EmitConditions(whereConds, engine, indent + 1)}");
        }

        // ── GROUP BY ──────────────────────────────────────────────────────────
        if (sel.GroupByColumns.Count > 0)
        {
            sb.Append($"\n{tabs}group by");
            if (sel.GroupByColumns.Count == 1)
            {
                sb.Append($" {RuleHelpers.EmitExpr(sel.GroupByColumns[0], engine, indent)}");
            }
            else
            {
                for (int gi = 0; gi < sel.GroupByColumns.Count; gi++)
                {
                    sb.Append($"\n{tabs}\t{RuleHelpers.EmitExpr(sel.GroupByColumns[gi], engine, indent + 1)}");
                    if (gi < sel.GroupByColumns.Count - 1) sb.Append(",");
                }
            }
        }

        // ── HAVING ────────────────────────────────────────────────────────────
        if (havingCond != null)
            sb.Append($"\n{tabs}having {RuleHelpers.EmitExpr(havingCond.Expression, engine, indent)}");

        // ── ORDER BY ─────────────────────────────────────────────────────────
        if (sel.OrderByColumns.Count > 0)
        {
            var orderStr = string.Join(", ", sel.OrderByColumns.Select(o => RuleHelpers.EmitExpr(o, engine, indent)));
            sb.Append($"\n{tabs}order by {orderStr}");
        }

        return sb.ToString();
    }

}

}
