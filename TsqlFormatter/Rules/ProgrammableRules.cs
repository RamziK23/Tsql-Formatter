using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// CREATE/ALTER FUNCTION|PROCEDURE header:
///   create or alter function dbo.f (
///       @a int,        -- comment
///       @b float = 1
///   )
///   returns int
///   as
///   &lt;body&gt;
/// The opening paren stays on the name line; each parameter on its own indented line.
/// </summary>
public sealed class ProgrammableObjectRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is ProgrammableObjectNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var o = (ProgrammableObjectNode)node;
        var tabs = RuleHelpers.Tabs(indent);
        var sb = new StringBuilder();

        sb.Append($"{tabs}{o.Prefix} {o.Name}");
        if (o.Params.Count > 0 || o.HasParens)
        {
            if (o.HasParens) sb.Append(" (");
            for (int i = 0; i < o.Params.Count; i++)
            {
                var p       = o.Params[i];
                var type    = RuleHelpers.EmitTypeTokens(p.DataType);
                var def     = p.Default != null ? $" = {RuleHelpers.EmitExpr(p.Default, engine, indent + 1)}" : "";
                var outp    = p.Output ? " output" : "";
                var comma   = i < o.Params.Count - 1 ? "," : "";
                var comment = p.TrailingComment != null ? RuleHelpers.TrailingCommentSuffix(p.TrailingComment) : "";
                sb.Append($"\n{tabs}\t{p.Variable.Value} {type}{def}{outp}{comma}{comment}");
            }
            if (o.HasParens) sb.Append($"\n{tabs})");
        }

        if (o.ReturnsClause.Count > 0)
            sb.Append($"\n{tabs}{RuleHelpers.EmitRawTokens(o.ReturnsClause)}");

        sb.Append($"\n{tabs}as");
        if (o.Body != null)
            sb.Append($"\n{engine.Format(o.Body, indent)}");
        return sb.ToString();
    }
}

/// <summary>IF / WHILE / SET / RETURN. IF and WHILE conditions go each on their own indented
/// line (like WHERE); the body/statement follows on its own line.</summary>
public sealed class ControlFlowRule : IFormatterRule
{
    public bool CanHandle(AstNode node) =>
        node is IfNode || node is WhileNode || node is SetNode || node is ReturnNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var tabs = RuleHelpers.Tabs(indent);
        switch (node)
        {
            case IfNode iff:
            {
                var sb = new StringBuilder();
                // The first condition stays on the "if" line; the rest follow one tab in.
                var inline = RuleHelpers.EmitConditionsInline(iff.Conditions, engine, indent);
                if (inline != null)
                    sb.Append($"{tabs}if {inline}");
                else
                    sb.Append($"{tabs}if\n{RuleHelpers.EmitConditions(iff.Conditions, engine, indent + 1)}");

                if (iff.Then != null) sb.Append(RuleHelpers.EmitBranchBody(iff.Then, engine, indent));
                if (iff.Else != null)
                {
                    sb.Append($"\n{tabs}else");
                    sb.Append(RuleHelpers.EmitBranchBody(iff.Else, engine, indent));
                }
                return sb.ToString();
            }
            case WhileNode wh:
            {
                // Same layout as IF: first condition on the keyword's line, the rest one tab in,
                // body on the next line at the loop's own indent.
                var sb = new StringBuilder();
                var inline = RuleHelpers.EmitConditionsInline(wh.Conditions, engine, indent);
                if (inline != null)
                    sb.Append($"{tabs}while {inline}");
                else
                    sb.Append($"{tabs}while\n{RuleHelpers.EmitConditions(wh.Conditions, engine, indent + 1)}");

                if (wh.Body != null) sb.Append(RuleHelpers.EmitBranchBody(wh.Body, engine, indent));
                return sb.ToString();
            }
            case SetNode st:
            {
                var target = RuleHelpers.EmitExpr(st.Target, engine, indent);
                var val    = RuleHelpers.EmitExpr(st.Value,  engine, indent);
                return $"{tabs}set {target} {st.Op} {val}";
            }
            case ReturnNode ret:
            {
                var val = ret.Value != null ? " " + RuleHelpers.EmitExpr(ret.Value, engine, indent) : "";
                return $"{tabs}return{val}";
            }
        }
        return "";
    }
}

}
