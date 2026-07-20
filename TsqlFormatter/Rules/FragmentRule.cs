using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// Formats partial selections (fragments) that are not complete statements:
///   - a bare WHERE clause (or a bare condition list),
///   - a bare column list,
///   - a bare JOIN chain,
///   - a bare GROUP BY / ORDER BY list.
/// Rendering mirrors how the same construct is emitted inside a full SELECT, so a
/// formatted fragment can be pasted back without re-indenting.
/// </summary>
public sealed class FragmentRule : IFormatterRule
{
    public bool CanHandle(AstNode node) =>
        node is WhereFragmentNode || node is ColumnListFragmentNode || node is JoinFragmentNode
        || node is GroupByFragmentNode || node is OrderByFragmentNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        return node switch
        {
            WhereFragmentNode w      => FormatWhere(w, engine, indent),
            ColumnListFragmentNode c => FormatColumns(c, engine, indent),
            JoinFragmentNode j       => FormatJoins(j, engine, indent),
            GroupByFragmentNode g    => FormatGroupBy(g, engine, indent),
            OrderByFragmentNode o    => FormatOrderBy(o, engine, indent),
            _                        => ""
        };
    }

    // ── WHERE fragment — identical layout to SelectRule's WHERE block ─────────
    private static string FormatWhere(WhereFragmentNode w, FormatterEngine engine, int indent)
    {
        var tabs = RuleHelpers.Tabs(indent);
        var sb   = new StringBuilder();
        var conds = w.Conditions;

        sb.Append($"{tabs}where");
        if (conds.Count == 1 && conds[0] is ConditionNode sc && sc.LogicalOp == null
            && sc.Expression is not ConditionGroupNode)
        {
            var cmt = sc.TrailingComment != null ? $" {sc.TrailingComment}" : "";
            sb.Append($" {RuleHelpers.EmitExpr(sc.Expression, engine, indent)}{cmt}");
        }
        else
            sb.Append($"\n{RuleHelpers.EmitConditions(conds, engine, indent + 1)}");
        return sb.ToString();
    }

    // ── Column-list fragment — same as SelectRule's column block (no "select") ─
    private static string FormatColumns(ColumnListFragmentNode c, FormatterEngine engine, int indent)
    {
        var tabs = RuleHelpers.Tabs(indent);
        var sb   = new StringBuilder();
        for (int i = 0; i < c.Columns.Count; i++)
        {
            var col     = c.Columns[i];
            var colStr  = RuleHelpers.EmitExpr(col.Expression, engine, indent);
            var alias   = col.Alias != null ? $" as {col.Alias.Value}" : "";
            var comma   = i < c.Columns.Count - 1 ? "," : "";
            var comment = col.TrailingComment != null ? $"{RuleHelpers.Tabs(2)}{col.TrailingComment}" : "";
            var leading = col.LeadingComment  != null ? $"{col.LeadingComment} " : "";
            if (i > 0) sb.Append('\n');
            sb.Append($"{tabs}{leading}{colStr}{alias}{comma}{comment}");
        }
        return sb.ToString();
    }

    // ── JOIN fragment — reuse the shared JOIN formatter ──────────────────────
    private static string FormatJoins(JoinFragmentNode j, FormatterEngine engine, int indent)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < j.Joins.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            // FormatJoin prefixes a leading "\n{tab}" assuming it follows a FROM line;
            // trim that so the fragment starts cleanly at the requested indent.
            var s = RuleHelpers.FormatJoin(j.Joins[i], engine, indent).TrimStart('\n');
            sb.Append(s);
        }
        return sb.ToString();
    }

    // ── GROUP BY fragment — each column on its own line (matches SelectRule) ──
    private static string FormatGroupBy(GroupByFragmentNode g, FormatterEngine engine, int indent)
    {
        var tabs = RuleHelpers.Tabs(indent);
        var sb   = new StringBuilder();
        sb.Append($"{tabs}group by");
        if (g.Columns.Count == 1)
        {
            sb.Append($" {RuleHelpers.EmitExpr(g.Columns[0], engine, indent)}");
        }
        else
        {
            for (int i = 0; i < g.Columns.Count; i++)
            {
                sb.Append($"\n{tabs}\t{RuleHelpers.EmitExpr(g.Columns[i], engine, indent + 1)}");
                if (i < g.Columns.Count - 1) sb.Append(",");
            }
        }
        return sb.ToString();
    }

    // ── ORDER BY fragment — single line (matches SelectRule) ─────────────────
    private static string FormatOrderBy(OrderByFragmentNode o, FormatterEngine engine, int indent)
    {
        var tabs = RuleHelpers.Tabs(indent);
        var items = string.Join(", ", o.Items.Select(x => RuleHelpers.EmitExpr(x, engine, indent)));
        return $"{tabs}order by {items}";
    }
}

}
