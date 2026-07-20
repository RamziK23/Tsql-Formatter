using System.Collections.Generic;
using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// INSERT INTO table [(col1, col2, ...)]
/// VALUES (v1, v2), (v3, v4)
/// — or —
/// INSERT INTO table [(cols)]
/// SELECT ...
/// </summary>
public sealed class InsertRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is InsertNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var ins  = (InsertNode)node;
        var tabs = RuleHelpers.Tabs(indent);
        var sb   = new System.Text.StringBuilder();

        sb.Append($"{tabs}insert into {RuleHelpers.EmitTableRef(ins.Table, engine, indent)}");

        if (ins.Columns.Count > 0)
        {
            var cols = string.Join(", ", ins.Columns.Select(c => RuleHelpers.EmitExpr(c, engine, indent)));
            sb.Append($" ({cols})");
        }

        sb.Append("\n");

        if (ins.Source is ValuesNode vn)
        {
            sb.Append($"{tabs}values");
            for (int r = 0; r < vn.Rows.Count; r++)
            {
                var vals = string.Join(", ", vn.Rows[r].Select(v => RuleHelpers.EmitExpr(v, engine, indent)));
                sb.Append($"\n{tabs}\t({vals})");
                if (r < vn.Rows.Count - 1) sb.Append(",");
            }
        }
        else if (ins.Source != null)
        {
            sb.Append(engine.Format(ins.Source, indent));
        }

        return sb.ToString();
    }
}

}
