using System.Linq;
using System.Collections.Generic;

namespace TsqlFormatter.Core
{

// ─── Base ────────────────────────────────────────────────────────────────────

public abstract class AstNode
{
    /// <summary>
    /// A -- line comment that trailed this statement on the SAME line as its last token.
    /// Kept here so the comment stays attached to its original line instead of migrating
    /// to the following statement. Rendered by FormatScript on the statement's last line.
    /// </summary>
    public string? StatementTrailingComment { get; set; }

    /// <summary>
    /// True when the source had a blank line (2+ newlines) before this statement.
    /// FormatScript uses it to preserve the original blank-line structure between
    /// statements instead of always forcing one.
    /// </summary>
    public bool BlankLineBefore { get; set; }

    /// <summary>
    /// True when the source had a ';' right before this statement AND the statement starts
    /// with WITH (a CTE). T-SQL requires the previous statement to be terminated with a
    /// semicolon before WITH, so dropping it would break the script — it is re-emitted
    /// as a ";with ..." prefix.
    /// </summary>
    public bool LeadingSemicolon { get; set; }
}

// ─── Script root ─────────────────────────────────────────────────────────────

public sealed class ScriptNode : AstNode
{
    public List<AstNode> Statements { get; } = new();
    /// <summary>True when the source started with ; before the first statement (e.g. ;WITH).</summary>
    public bool HasLeadingSemicolon { get; set; }
    /// <summary>
    /// Source text of a construct that runs off the end of the input (a selection cut mid-statement,
    /// e.g. "… from openquery(" with the rest missing). It is emitted verbatim after the statements
    /// that did parse, so an unfinished tail costs only its own formatting — not the whole script's.
    /// </summary>
    public string? UnparsedTail { get; set; }
    /// <summary>True when a blank line separated the unparsed tail from the previous statement.</summary>
    public bool UnparsedTailBlankBefore { get; set; }
    /// <summary>
    /// True when the tail is the leftover of a statement whose earlier part was formatted: it
    /// starts with the original whitespace (or none) that stood between the two, so it is appended
    /// as-is — "… from openquery(" keeps the unfinished call on the line it was written on.
    /// </summary>
    public bool UnparsedTailGlued { get; set; }
}

// ─── Statements ──────────────────────────────────────────────────────────────

/// <summary>Single variable inside a DECLARE statement.</summary>
public sealed class DeclareVarNode : AstNode
{
    public Token Variable { get; init; } = null!;
    public List<Token> DataType { get; } = new();
    public AstNode? Initializer { get; init; }
    public string? TrailingComment { get; set; }
}

/// <summary>DECLARE statement — one or more comma-separated variables.</summary>
public sealed class DeclareNode : AstNode
{
    public List<DeclareVarNode> Variables { get; } = new();
}

public sealed class SelectStatementNode : AstNode
{
    public string? TopExpr { get; init; }
    public bool IsDistinct { get; init; }
    /// <summary>Target of SELECT ... INTO #tbl (table name), or null.</summary>
    public string? IntoTable { get; set; }
    public List<SelectColumnNode> Columns { get; } = new();
    public List<AstNode> FromClauses { get; } = new();
    public List<AstNode> WhereConditions { get; } = new();
    public List<AstNode> GroupByColumns { get; } = new();
    /// <summary>HAVING conditions: first item without operator, the rest with and/or.</summary>
    public List<AstNode> HavingConditions { get; } = new();
    public List<AstNode> OrderByColumns { get; } = new();
    public List<AstNode> CteDefinitions { get; } = new();
    /// <summary>Standalone comments that trailed the FROM/JOIN block (before WHERE/GROUP/etc).</summary>
    public List<string> PostFromComments { get; } = new();
    /// <summary>Comments that preceded the SELECT keyword (e.g. inside "( --note\n select ...)").
    /// Rendered on their own line(s) above the select header.</summary>
    public List<string> LeadingComments { get; } = new();
    /// <summary>Query hint clause: OPTION (...). Tokens inside the parens (excluding them),
    /// or null when absent. Rendered as a trailing "option(...)" line, never as a condition.</summary>
    public List<Token>? OptionTokens { get; set; }
}

/// <summary>
/// UNION / EXCEPT / INTERSECT between two SELECT statements.
/// </summary>
public sealed class SetOperationNode : AstNode
{
    /// <summary>UNION, UNION ALL, EXCEPT, INTERSECT</summary>
    public string Operator { get; init; } = "UNION";
    public AstNode Left { get; init; } = null!;
    public AstNode Right { get; init; } = null!;
}

// ─── INSERT ──────────────────────────────────────────────────────────────────

public sealed class InsertNode : AstNode
{
    public TableRefNode Table { get; init; } = null!;
    /// <summary>Explicit column list after INSERT INTO t (...), may be empty.</summary>
    public List<AstNode> Columns { get; } = new();
    /// <summary>Either ValuesNode or SelectStatementNode / SetOperationNode.</summary>
    public AstNode? Source { get; init; }
    /// <summary>A -- comment on the closing paren's line: "insert into t (…)  -- note".</summary>
    public string? ColumnsComment { get; set; }
}

public sealed class ValuesNode : AstNode
{
    /// <summary>One list of expressions per VALUES row.</summary>
    public List<List<AstNode>> Rows { get; } = new();
}

// ─── UPDATE ──────────────────────────────────────────────────────────────────

public sealed class UpdateNode : AstNode
{
    public TableRefNode Table { get; init; } = null!;
    public List<AssignmentNode> Assignments { get; } = new();
    /// <summary>FROM clauses (table refs + joins), same shape as SelectStatementNode.</summary>
    public List<AstNode> FromClauses { get; } = new();
    public List<AstNode> WhereConditions { get; } = new();
}

public sealed class AssignmentNode : AstNode
{
    public AstNode Target { get; init; } = null!;   // column ref
    public AstNode Value  { get; init; } = null!;
}

// ─── DELETE ──────────────────────────────────────────────────────────────────

public sealed class DeleteNode : AstNode
{
    public TableRefNode? TargetAlias { get; init; }   // DELETE alias FROM …
    public TableRefNode? Table { get; init; }          // DELETE FROM tbl
    public List<AstNode> FromClauses { get; } = new();
    public List<AstNode> WhereConditions { get; } = new();
}

// ─── SELECT columns ──────────────────────────────────────────────────────────

public sealed class SelectColumnNode : AstNode
{
    public AstNode Expression { get; init; } = null!;
    public Token? Alias { get; init; }
    public bool CommaLeading { get; init; }
    public string? TrailingComment { get; set; }
    /// <summary>Comments before the column. A /* */ block comment renders inline before the
    /// expression (/* note */ expr); a -- line comment renders on its own line above the
    /// column (it can never precede an expression inline, or the expression is commented out).</summary>
    public List<string> LeadingComments { get; } = new();
}

// ─── FROM / JOIN ─────────────────────────────────────────────────────────────

public sealed class TableRefNode : AstNode
{
    public List<Token> Name { get; } = new();
    public Token? Alias { get; init; }
    public string? HintNolock { get; init; }
    /// <summary>A -- comment on the same line as this table reference in a FROM clause.</summary>
    public string? TrailingComment { get; set; }
    /// <summary>Subquery used as a table source: (SELECT ...) AS alias</summary>
    public SubQueryNode? SubQuery { get; init; }
    /// <summary>Arguments for function-valued table sources: func(arg1, arg2) AS alias</summary>
    public List<AstNode>? FuncArgs { get; init; }
    /// <summary>True when this is an OPENQUERY(server, 'remote sql') table source (rule 7).</summary>
    public bool IsOpenQuery { get; init; }
}

public sealed class JoinNode : AstNode
{
    public string JoinType { get; init; } = "INNER JOIN";
    public TableRefNode Table { get; init; } = null!;
    public List<AstNode> Conditions { get; } = new();
    /// <summary>Standalone comments that appeared on their own line(s) before this join.</summary>
    public List<string> LeadingComments { get; } = new();
}

// ─── WHERE / conditions ──────────────────────────────────────────────────────

public sealed class ConditionNode : AstNode
{
    public string? LogicalOp { get; init; }
    public AstNode Expression { get; init; } = null!;
    /// <summary>Inline comment on the same line, after the condition expression.</summary>
    public string? TrailingComment { get; set; }
    /// <summary>Standalone comments on their own line(s) before this condition (e.g. a
    /// commented-out "--and x = 1"). Rendered above the condition, never inline.</summary>
    public List<string> LeadingComments { get; } = new();
}

// ─── Expressions ─────────────────────────────────────────────────────────────

public sealed class BinaryExprNode : AstNode
{
    public AstNode Left { get; init; } = null!;
    public Token Op { get; init; } = null!;
    public AstNode Right { get; init; } = null!;
}

/// <summary>A parenthesised expression: ( expr ). Preserves grouping like ((a+b)*(c-d)).</summary>
public sealed class ParenExprNode : AstNode
{
    public AstNode Inner { get; init; } = null!;
}

/// <summary>A parenthesised boolean group: ( cond and cond or cond ). Preserves AND/OR structure.</summary>
public sealed class ConditionGroupNode : AstNode
{
    public List<AstNode> Conditions { get; } = new();
}

/// <summary>An ORDER BY item: expression plus optional ASC/DESC direction.</summary>
public sealed class OrderByItemNode : AstNode
{
    public AstNode Expression { get; init; } = null!;
    public string? Direction { get; init; }  // "asc" | "desc" | null
}

public sealed class InExprNode : AstNode
{
    public AstNode Left { get; init; } = null!;
    public bool Negated { get; init; }
    public List<AstNode> Values { get; } = new();
    /// <summary>Set when the IN list is a subquery: x in (select ...).</summary>
    public SubQueryNode? SubQuery { get; init; }
    /// <summary>True when a -- comment forces the list onto separate lines. A /* */ block
    /// comment is transparent and keeps the list inline, so it does not count here.</summary>
    public bool HasComments => Values.Any(v =>
        v is CommentedValueNode ||
        (v is InValueGroupNode g && g.TrailingLineComment != null));
}

public sealed class CommentedValueNode : AstNode
{
    public AstNode Value { get; init; } = null!;
    public string? TrailingComment { get; init; }
}

public sealed class BetweenExprNode : AstNode
{
    public AstNode Left { get; init; } = null!;
    public AstNode Low { get; init; } = null!;
    public AstNode High { get; init; } = null!;
}

public sealed class LikeExprNode : AstNode
{
    public AstNode Left { get; init; } = null!;
    public AstNode Pattern { get; init; } = null!;
}

public sealed class IsNullExprNode : AstNode
{
    public AstNode Left { get; init; } = null!;
    public bool IsNotNull { get; init; }
}

public sealed class SubQueryNode : AstNode
{
    public AstNode Select { get; init; } = null!;   // SelectStatementNode or SetOperationNode
    /// <summary>A comment on the SAME line as the opening '(' (e.g. "exists ( --note").
    /// Rendered right after the '(', so it stays on that line instead of above the select.</summary>
    public string? OpenComment { get; set; }
}

public sealed class FunctionCallNode : AstNode
{
    public string Name { get; init; } = string.Empty;
    /// <summary>True when the function name was a SQL keyword (built-in). Emit lowercase.</summary>
    public bool IsKeywordFunction { get; init; }
    public List<AstNode> Arguments { get; } = new();
    /// <summary>Window function OVER (...) clause, if any.</summary>
    public AstNode? OverClause { get; init; }
    /// <summary>Set quantifier inside an aggregate: "distinct" or "all" (e.g. count(distinct x)).</summary>
    public string? SetQuantifier { get; init; }
    /// <summary>True for a negated EXISTS: renders "not exists (...)" as a single construct.</summary>
    public bool Negated { get; init; }
}

/// <summary>Unary boolean negation: NOT (condition/group). Kept as one node so a condition
/// list never splits "not" and its operand into two separate conditions.</summary>
public sealed class NotExprNode : AstNode
{
    public AstNode Inner { get; init; } = null!;
}

public sealed class CaseExprNode : AstNode
{
    public AstNode? InputExpr { get; init; }
    public List<WhenClauseNode> WhenClauses { get; } = new();
    public AstNode? ElseExpr { get; init; }
    public string? ElseComment { get; init; }
}

public sealed class WhenClauseNode : AstNode
{
    public List<AstNode> Conditions { get; } = new();
    public AstNode Then { get; init; } = null!;
    public string? ThenComment { get; init; }
}

public sealed class LiteralNode : AstNode
{
    public Token Token { get; init; } = null!;
    public string? TrailingComment { get; set; }
}

public sealed class ColumnRefNode : AstNode
{
    public List<Token> Parts { get; } = new();
    public string? TrailingComment { get; set; }
}

public sealed class GoSeparatorNode : AstNode
{
    /// <summary>Optional repeat count on the same line: "GO 5" runs the batch 5 times.
    /// Must be preserved, or the batch silently stops repeating.</summary>
    public string? Count { get; set; }
}

// ─── Fragment nodes (for formatting partial selections) ──────────────────────

/// <summary>A bare WHERE fragment: "where a = 1 and b = 2" (with or without leading 'where').</summary>
public sealed class WhereFragmentNode : AstNode
{
    public List<AstNode> Conditions { get; } = new List<AstNode>();
}

/// <summary>A bare column-list fragment: "a, b, count(*) as cnt".</summary>
public sealed class ColumnListFragmentNode : AstNode
{
    public List<SelectColumnNode> Columns { get; } = new List<SelectColumnNode>();
}

/// <summary>A bare JOIN fragment: "inner join t on t.id = x.id".</summary>
public sealed class JoinFragmentNode : AstNode
{
    public List<JoinNode> Joins { get; } = new List<JoinNode>();
}

/// <summary>A bare GROUP BY fragment: "group by a, b, c".</summary>
public sealed class GroupByFragmentNode : AstNode
{
    public List<AstNode> Columns { get; } = new List<AstNode>();
}

/// <summary>A bare ORDER BY fragment: "order by a desc, b".</summary>
public sealed class OrderByFragmentNode : AstNode
{
    public List<AstNode> Items { get; } = new List<AstNode>();
}

// ─── BEGIN / END ─────────────────────────────────────────────────────────────

/// <summary>BEGIN ... END block. Rule 2.8: blank line after begin, before end; content +1 tab.</summary>
public sealed class BeginEndNode : AstNode
{
    public List<AstNode> Body { get; } = new List<AstNode>();
    /// <summary>"try" or "catch" for BEGIN TRY … END TRY / BEGIN CATCH … END CATCH; null for a
    /// plain BEGIN … END block.</summary>
    public string? Label { get; init; }
}

// ─── CREATE TABLE / DROP TABLE ───────────────────────────────────────────────

public sealed class ColumnDefNode : AstNode
{
    /// <summary>Column name token.</summary>
    public string Name { get; init; } = "";
    /// <summary>Full definition after the name: type + constraints (e.g. "varchar(255)" or "int not null default 0").</summary>
    public string Definition { get; init; } = "";
}

public sealed class CreateTableNode : AstNode
{
    public string TableName { get; init; } = "";
    public List<ColumnDefNode> Columns { get; } = new List<ColumnDefNode>();
}

public sealed class DropTableNode : AstNode
{
    public bool IfExists { get; init; }
    public string TableName { get; init; } = "";
}

// ─── IN value group (rule 2.3.2.3) ──────────────────────────────────────────

/// <summary>
/// A group of values inside an IN list that share one comment.
/// Examples:
///   44199, 431064, 9730,  --УТВ
///   /*ктв*/44199, 431064
/// </summary>
/// <summary>
/// A GROUP BY / ORDER BY item together with the standalone comments written above it. A -- comment
/// on its own line is not an expression, so without this it aborted the parse of the whole script.
/// </summary>
public sealed class ListItemNode : AstNode
{
    public AstNode Expression { get; init; } = null!;
    public List<string> LeadingComments { get; } = new();
}

public sealed class InValueGroupNode : AstNode
{
    public List<AstNode> Values { get; } = new List<AstNode>();
    /// <summary>Leading /*block*/ comment before the first value.</summary>
    public string? LeadingBlockComment { get; init; }
    /// <summary>Trailing -- line comment after the last value.</summary>
    public string? TrailingLineComment { get; init; }
}

// ─── Programmable objects (CREATE/ALTER FUNCTION / PROCEDURE) ─────────────────

/// <summary>A single parameter of a function/procedure: @name type [= default] [output].</summary>
public sealed class ParamNode : AstNode
{
    public Token Variable { get; init; } = null!;
    public List<Token> DataType { get; } = new();
    public AstNode? Default { get; init; }
    public bool Output { get; init; }
    public string? TrailingComment { get; set; }
}

/// <summary>CREATE/ALTER FUNCTION|PROCEDURE with a parameter list and a procedural body.</summary>
public sealed class ProgrammableObjectNode : AstNode
{
    public string Prefix { get; init; } = "";              // e.g. "create or alter function"
    public string Name { get; init; } = "";                // e.g. "dbo.fnErlangAgents"
    public bool HasParens { get; init; }                   // parameters were parenthesised
    public List<ParamNode> Params { get; } = new();
    public List<Token> ReturnsClause { get; } = new();     // tokens between ')' and AS (e.g. "returns int")
    public AstNode? Body { get; init; }                    // usually a BeginEndNode
}

/// <summary>IF &lt;conditions&gt; &lt;then&gt; [ELSE &lt;else&gt;].</summary>
public sealed class IfNode : AstNode
{
    public List<AstNode> Conditions { get; } = new();
    public AstNode? Then { get; init; }
    public AstNode? Else { get; init; }
}

/// <summary>WHILE &lt;conditions&gt; &lt;body&gt;.</summary>
public sealed class WhileNode : AstNode
{
    public List<AstNode> Conditions { get; } = new();
    public AstNode? Body { get; init; }
}

/// <summary>SET @var {= | += | -= | …} &lt;value&gt;.</summary>
public sealed class SetNode : AstNode
{
    public AstNode Target { get; init; } = null!;
    public string Op { get; init; } = "=";
    public AstNode Value { get; init; } = null!;
}

/// <summary>RETURN [&lt;value&gt;].</summary>
public sealed class ReturnNode : AstNode
{
    public AstNode? Value { get; init; }
}

public sealed class RawTokensNode : AstNode
{
    public List<Token> Tokens { get; } = new();
    /// <summary>True if the original source had a blank line before this node.</summary>
    public bool BlankBefore { get; set; }
    /// <summary>True if the original source had a blank line after this node.</summary>
    public bool BlankAfter { get; set; }
}

public sealed class CteDefinitionNode : AstNode
{
    public Token Name { get; init; } = null!;
    /// <summary>SelectStatementNode or SetOperationNode.</summary>
    public AstNode Body { get; init; } = null!;
}

}
