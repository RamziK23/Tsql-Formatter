using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// Rule: DECLARE — the first variable stays on the "declare" line, every following one moves
/// to its own line one tab in.
///   declare @var1 type = value,
///       @var2 type
/// </summary>
public sealed class DeclareRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is DeclareNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var d    = (DeclareNode)node;
        var tabs = RuleHelpers.Tabs(indent);
        var sb   = new System.Text.StringBuilder();

        sb.Append($"{tabs}declare");

        // First variable on the declare line; each following one on its own line (+1 tab).
        for (int i = 0; i < d.Variables.Count; i++)
        {
            var v        = d.Variables[i];
            var lead     = i == 0 ? " " : $"\n{tabs}\t";
            // A table variable's type is a column list, laid out like CREATE TABLE.
            if (v.TableColumns != null)
            {
                sb.Append($"{lead}{v.Variable.Value} table (\n");
                sb.Append(RuleHelpers.EmitColumnDefs(v.TableColumns, indent));
                sb.Append($"{tabs})");
                if (i < d.Variables.Count - 1) sb.Append(",");
                if (v.TrailingComment != null) sb.Append(RuleHelpers.TrailingCommentSuffix(v.TrailingComment));
                continue;
            }
            var dataType = RuleHelpers.EmitTypeTokens(v.DataType);
            var init     = v.Initializer != null
                ? $" = {RuleHelpers.EmitExpr(v.Initializer, engine, indent + 1)}"
                : "";
            sb.Append($"{lead}{v.Variable.Value} {dataType}{init}");
            if (i < d.Variables.Count - 1) sb.Append(",");
            if (v.TrailingComment != null) sb.Append(RuleHelpers.TrailingCommentSuffix(v.TrailingComment));
        }

        return sb.ToString();
    }
}

}
