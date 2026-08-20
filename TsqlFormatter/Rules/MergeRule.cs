using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// Rule `merge`:
///   merge &lt;target&gt;
///   using &lt;source&gt;
///       on &lt;first condition&gt;
///       and &lt;next condition&gt;
///   when matched and &lt;condition&gt;
///   then
///       update set
///           col = val
///   …
///   output … into …
/// The ON conditions sit one tab in, like a JOIN's; every WHEN starts its own line with its
/// first extra condition beside it (as in IF); THEN takes its own line and the action follows
/// one tab in.
/// </summary>
public sealed class MergeRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is MergeNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var m    = (MergeNode)node;
        var tabs = RuleHelpers.Tabs(indent);
        var t1   = RuleHelpers.Tabs(indent + 1);
        var t2   = RuleHelpers.Tabs(indent + 2);
        var sb   = new StringBuilder();

        var into = m.HasInto ? "into " : "";
        sb.Append($"{tabs}merge {into}{RuleHelpers.EmitTableRef(m.Target, engine, indent)}");
        if (m.TargetComment != null) sb.Append(RuleHelpers.TrailingCommentSuffix(m.TargetComment));

        sb.Append($"\n{tabs}using {RuleHelpers.EmitTableRef(m.Source, engine, indent)}");
        if (m.SourceComment != null) sb.Append(RuleHelpers.TrailingCommentSuffix(m.SourceComment));

        // ON: first condition on the "on" line, the rest one tab further — the JOIN layout.
        for (int i = 0; i < m.OnConditions.Count; i++)
        {
            var c    = m.OnConditions[i] as ConditionNode;
            var text = RuleHelpers.EmitExpr(c?.Expression ?? m.OnConditions[i], engine, indent + 2);
            var cmt  = c?.TrailingComment != null ? $" {c.TrailingComment}" : "";
            sb.Append(i == 0
                ? $"\n{t1}on {text}{cmt}"
                : $"\n{t2}{c?.LogicalOp ?? "and"} {text}{cmt}");
        }

        foreach (var w in m.Whens)
        {
            sb.Append($"\n{tabs}when {w.Kind}");
            // Extra AND/OR conditions: the first stays on the when line, like IF.
            for (int i = 0; i < w.ExtraConditions.Count; i++)
            {
                var c    = w.ExtraConditions[i] as ConditionNode;
                var text = RuleHelpers.EmitExpr(c?.Expression ?? w.ExtraConditions[i], engine, indent + 1);
                var op   = c?.LogicalOp ?? "and";
                sb.Append(i == 0 ? $" {op} {text}" : $"\n{t1}{op} {text}");
            }
            if (w.ConditionComment != null) sb.Append(RuleHelpers.LineClosingCommentSuffix(w.ConditionComment));
            sb.Append($"\n{tabs}then");
            if (w.ThenComment != null) sb.Append(RuleHelpers.TrailingCommentSuffix(w.ThenComment));
            sb.Append(EmitAction(w, engine, indent + 1));
        }

        if (m.OutputTokens != null)
        {
            sb.Append($"\n{tabs}{RuleHelpers.EmitRawTokens(m.OutputTokens)}");
            if (m.OutputComment != null) sb.Append(RuleHelpers.TrailingCommentSuffix(m.OutputComment));
        }
        if (m.OutputIntoTokens != null)
        {
            sb.Append($"\n{tabs}{RuleHelpers.EmitRawTokens(m.OutputIntoTokens)}");
            if (m.OutputIntoComment != null) sb.Append(RuleHelpers.TrailingCommentSuffix(m.OutputIntoComment));
        }

        return sb.ToString();
    }

    private static string EmitAction(MergeWhenNode w, FormatterEngine engine, int indent)
    {
        var tabs = RuleHelpers.Tabs(indent);
        var sb   = new StringBuilder();
        switch (w.Action)
        {
            case "update":
                // "update set" opens the list; each assignment on its own line, the comma before
                // its comment.
                sb.Append($"\n{tabs}update set");
                for (int i = 0; i < w.Assignments.Count; i++)
                {
                    var a      = w.Assignments[i];
                    var target = RuleHelpers.EmitExpr(a.Target, engine, indent + 1);
                    var value  = RuleHelpers.EmitExpr(a.Value,  engine, indent + 1);
                    sb.Append($"\n{RuleHelpers.Tabs(indent + 1)}{target} = {value}");
                    if (i < w.Assignments.Count - 1) sb.Append(",");
                    if (a.TrailingComment != null) sb.Append(RuleHelpers.TrailingCommentSuffix(a.TrailingComment));
                }
                break;

            case "insert":
                sb.Append($"\n{tabs}insert");
                if (w.InsertColumns.Count > 0)
                {
                    sb.Append(" (\n");
                    for (int i = 0; i < w.InsertColumns.Count; i++)
                    {
                        sb.Append($"{RuleHelpers.Tabs(indent + 1)}{RuleHelpers.EmitExpr(w.InsertColumns[i], engine, indent + 1)}");
                        if (i < w.InsertColumns.Count - 1) sb.Append(",");
                        sb.Append("\n");
                    }
                    sb.Append($"{tabs})");
                }
                if (w.DefaultValues) sb.Append(" default values");
                else if (w.InsertValues is ValuesNode vn)
                {
                    sb.Append($"\n{tabs}values");
                    for (int r = 0; r < vn.Rows.Count; r++)
                    {
                        var vals = string.Join(", ", vn.Rows[r].Select(v => RuleHelpers.EmitExpr(v, engine, indent)));
                        sb.Append($"\n{RuleHelpers.Tabs(indent + 1)}({vals})");
                        if (r < vn.Rows.Count - 1) sb.Append(",");
                        if (r < vn.RowComments.Count && vn.RowComments[r] != null)
                            sb.Append(RuleHelpers.TrailingCommentSuffix(vn.RowComments[r]!));
                    }
                }
                break;

            default:
                sb.Append($"\n{tabs}delete");
                break;
        }
        return sb.ToString();
    }
}

}
