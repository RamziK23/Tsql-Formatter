using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// Rule: DECLARE with variables each on its own indented line.
///   declare
///       @var1 type = value,
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

        // Every variable on its own indented line, even a single one.
        for (int i = 0; i < d.Variables.Count; i++)
        {
            var v        = d.Variables[i];
            var dataType = RuleHelpers.EmitTypeTokens(v.DataType);
            var init     = v.Initializer != null
                ? $" = {RuleHelpers.EmitExpr(v.Initializer, engine, indent + 1)}"
                : "";
            sb.Append($"\n{tabs}\t{v.Variable.Value} {dataType}{init}");
            if (i < d.Variables.Count - 1) sb.Append(",");
            if (v.TrailingComment != null) sb.Append(RuleHelpers.TrailingCommentSuffix(v.TrailingComment));
        }

        return sb.ToString();
    }
}

}
