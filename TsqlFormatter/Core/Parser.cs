using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TsqlFormatter.Core
{

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;
    // Comments skipped by Expect() while re-synchronizing; drained onto the next node
    // (e.g. a subquery SELECT) so they are never silently lost.
    private readonly List<string> _pendingComments = new();

    private static readonly HashSet<TokenType> Skippable = new()
    {
        TokenType.Whitespace, TokenType.Newline
    };

    public Parser(List<Token> tokens) { _tokens = tokens; }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Script
    // ═══════════════════════════════════════════════════════════════════════════

    public ScriptNode Parse()
    {
        var script = new ScriptNode();
        bool isFirstStatement = true;

        while (!IsAtEnd())
        {
            bool hadSemicolon = false;
            int newlinesBefore = 0;
            // Consume leading separators. Each GO keyword emits its OWN separator node,
            // so consecutive GO batch separators are preserved rather than collapsed to one.
            while (!IsAtEnd())
            {
                var raw = PeekRaw();
                if (raw.Type == TokenType.Semicolon)
                    { hadSemicolon = true; AdvanceRaw(); continue; }
                if (raw.Type == TokenType.Newline)
                    { newlinesBefore++; AdvanceRaw(); continue; }
                if (raw.Type == TokenType.Whitespace)
                    { AdvanceRaw(); continue; }
                if (IsGoKeyword())
                {
                    AdvanceRaw();
                    // A GO before any statement is meaningless — drop it.
                    if (script.Statements.Count > 0)
                        script.Statements.Add(new GoSeparatorNode());
                    continue;
                }
                break;
            }

            // Only trailing separators remained — stop.
            if (IsAtEnd())
                break;

            // Leading semicolon before first statement (e.g. ;with ...)
            if (isFirstStatement && hadSemicolon)
                script.HasLeadingSemicolon = true;
            isFirstStatement = false;

            var stmt = ParseStatement();
            if (stmt != null)
            {
                // Preserve a blank line that existed before this statement in the source.
                if (newlinesBefore >= 2 && script.Statements.Count > 0)
                    stmt.BlankLineBefore = true;
                // A -- comment on the SAME line as the statement's end is a trailing comment
                // for THIS statement — keep it attached here rather than letting it migrate
                // to the following statement as a standalone comment.
                var trailing = TryTakeSameLineComment();
                if (trailing != null) stmt.StatementTrailingComment = trailing;
                script.Statements.Add(stmt);
            }
        }
        return script;
    }

    /// <summary>
    /// Parses a partial SQL selection (fragment). Returns a fragment node when the input
    /// is a bare WHERE clause, column list, or JOIN chain; otherwise returns null so the
    /// caller can fall back to full-statement parsing.
    /// </summary>
    public AstNode? ParseFragment()
    {
        var first = Peek();

        // A leading standalone comment means this is (at least) a full statement with a
        // preceding comment, not a bare clause fragment. Let full parsing handle it.
        if (first.Type is TokenType.LineComment or TokenType.BlockComment)
            return null;

        // Bare WHERE clause: "where a = 1 and b = 2"
        if (first.IsKeyword("WHERE"))
        {
            Advance();
            var node = new WhereFragmentNode();
            node.Conditions.AddRange(ParseConditionList(isJoinOn: false));
            return AtFragmentEnd() ? node : null;
        }

        // Bare GROUP BY: "group by a, b, c"
        if (first.IsKeyword("GROUP"))
        {
            Advance();
            if (Peek().IsKeyword("BY")) Advance();
            var node = new GroupByFragmentNode();
            node.Columns.AddRange(ParseExpressionList());
            return AtFragmentEnd() ? node : null;
        }

        // Bare ORDER BY: "order by a desc, b asc"
        if (first.IsKeyword("ORDER"))
        {
            Advance();
            if (Peek().IsKeyword("BY")) Advance();
            var node = new OrderByFragmentNode();
            node.Items.AddRange(ParseOrderByList());
            return AtFragmentEnd() ? node : null;
        }

        // Bare JOIN chain: "inner join t on ... [join ...]"
        if (IsJoinKeyword())
        {
            var node = new JoinFragmentNode();
            while (IsJoinKeyword()) node.Joins.Add(ParseJoin());
            return AtFragmentEnd() ? node : null;
        }

        // If it starts with a statement keyword, this isn't a fragment.
        if (first.IsKeyword("SELECT") || first.IsKeyword("INSERT") || first.IsKeyword("UPDATE")
            || first.IsKeyword("DELETE") || first.IsKeyword("WITH") || first.IsKeyword("BEGIN")
            || first.IsKeyword("CREATE") || first.IsKeyword("DROP") || first.IsKeyword("DECLARE")
            || first.Type == TokenType.DeclareKeyword)
            return null;

        // Bare condition list (no leading WHERE): "a = 1 and b = 2".
        int save = _pos;
        try
        {
            var conds = ParseConditionList(isJoinOn: false);
            if (conds.Count > 0 && AtFragmentEnd() && LooksLikeConditions(conds))
            {
                var node = new WhereFragmentNode();
                node.Conditions.AddRange(conds);
                return node;
            }
        }
        catch { /* fall through */ }
        _pos = save;

        // Bare column list: "a, b, count(*) as cnt"
        save = _pos;
        try
        {
            var cols = ParseSelectColumns();
            if (cols.Count > 0 && AtFragmentEnd())
            {
                var node = new ColumnListFragmentNode();
                node.Columns.AddRange(cols);
                return node;
            }
        }
        catch { /* fall through */ }
        _pos = save;

        return null;
    }

    /// <summary>True if only EOF remains (skippables are transparent to Peek).</summary>
    private bool AtFragmentEnd() => Peek().Type == TokenType.EndOfFile;

    /// <summary>Heuristic: a condition list is "real" if at least one item is a comparison/logical expr.</summary>
    private static bool LooksLikeConditions(List<AstNode> conds)
    {
        foreach (var c in conds)
        {
            if (c is ConditionNode cn)
            {
                var e = cn.Expression;
                if (e is BinaryExprNode || e is InExprNode || e is BetweenExprNode || e is LikeExprNode)
                    return true;
            }
        }
        return false;
    }

    private bool IsGoKeyword()
    {
        var t = Peek();
        return t.Type == TokenType.Identifier && t.Value.Equals("GO", StringComparison.OrdinalIgnoreCase);
    }

    private AstNode? ParseStatement()
    {
        var tok = Peek();
        if (tok.Type == TokenType.DeclareKeyword)  return ParseDeclare();
        if (tok.IsKeyword("WITH"))                 return ParseWithCte();
        if (tok.IsKeyword("SELECT"))               return ParseSelectOrSet(null);
        if (tok.IsKeyword("INSERT"))               return ParseInsert();
        if (tok.IsKeyword("UPDATE"))               return ParseUpdate();
        if (tok.IsKeyword("DELETE"))               return ParseDelete();
        if (tok.IsKeyword("BEGIN"))                return ParseBeginEnd();
        if (tok.IsKeyword("CREATE"))               return ParseCreate();
        if (tok.IsKeyword("DROP"))                 return ParseDrop();
        if (tok.Type == TokenType.Semicolon)        { Advance(); return null; }
        // Standalone comment: consume as single-token raw node (prevent bleeding into next statement)
        if (tok.Type is TokenType.LineComment or TokenType.BlockComment)
        {
            bool blankBefore = CountNewlinesBackFromCurrent() >= 2;
            var cmt = new RawTokensNode(); cmt.Tokens.Add(Advance());
            bool blankAfter = CountNewlinesForwardFromCurrent() >= 2;
            cmt.BlankBefore = blankBefore;
            cmt.BlankAfter = blankAfter;
            return cmt;
        }
        return ParseRawStatement();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DECLARE
    // ═══════════════════════════════════════════════════════════════════════════

    private DeclareNode ParseDeclare()
    {
        Advance(); // DECLARE keyword
        var node = new DeclareNode();
        do
        {
            var variable = Expect(TokenType.Variable);
            var dataType = new List<Token>();
            int typeDepth = 0;
            while (!IsAtEnd())
            {
                var t = Peek();
                // A comma or '=' ends the type only at paren depth 0
                if (typeDepth == 0 && (PeekIs(TokenType.Equals) || PeekIs(TokenType.Comma))) break;
                if (t.IsKeyword("SELECT") || t.Type == TokenType.DeclareKeyword
                    || IsGoKeyword() || t.Type == TokenType.EndOfFile) break;
                if (t.Type == TokenType.LeftParen)  typeDepth++;
                if (t.Type == TokenType.RightParen) typeDepth--;
                dataType.Add(Advance());
            }
            AstNode? init = null;
            if (TryConsume(TokenType.Equals)) init = ParseExpression();
            // Trailing comment before the comma: @a int = 1, --note
            string? varComment = null;
            if (PeekIs(TokenType.LineComment)) varComment = Advance().Value;
            node.Variables.Add(new DeclareVarNode { Variable = variable, Initializer = init, TrailingComment = varComment }
                .Tap(v => v.DataType.AddRange(dataType)));
            // A trailing comment may also sit after the comma: @a int = 1, --note
            if (PeekIs(TokenType.Comma))
            {
                // peek: comment right after comma belongs to the just-added variable if it had none
            }
        } while (PeekIs(TokenType.Comma) && ConsumeCommaThenMaybeComment(node));
        return node;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WITH / CTE
    // ═══════════════════════════════════════════════════════════════════════════

    private AstNode ParseWithCte()
    {
        Advance(); // WITH
        var ctes = new List<AstNode>();
        while (true)
        {
            var cteName = Advance();
            Expect(TokenType.Keyword, "AS");
            Expect(TokenType.LeftParen);
            var cteBody = ParseSelectOrSet(null);
            Expect(TokenType.RightParen);
            ctes.Add(new CteDefinitionNode { Name = cteName, Body = cteBody });
            if (!PeekIs(TokenType.Comma)) break;
            Advance();
        }
        return ParseSelectOrSet(ctes);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SELECT  (+ UNION / EXCEPT / INTERSECT chaining)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a single SELECT then checks for UNION / EXCEPT / INTERSECT.
    /// Returns either a SelectStatementNode or a SetOperationNode tree.
    /// CTEs are attached only to the outermost left node.
    /// </summary>
    private AstNode ParseSelectOrSet(List<AstNode>? ctes)
    {
        AstNode left = ParseSelectStatement(ctes);

        while (IsSetOperator())
        {
            string op = BuildSetOperator(); // "UNION" / "UNION ALL" / "EXCEPT" / "INTERSECT"
            AstNode right = ParseSelectStatement(null);
            left = new SetOperationNode { Operator = op, Left = left, Right = right };
        }

        return left;
    }

    private bool IsSetOperator()
    {
        var t = Peek();
        return t.IsKeyword("UNION") || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT");
    }

    private string BuildSetOperator()
    {
        var t = Advance(); // UNION / EXCEPT / INTERSECT
        string op = t.Value.ToLowerInvariant();
        if (op == "union" && Peek().IsKeyword("ALL")) { Advance(); op = "union all"; }
        return op;
    }

    private SelectStatementNode ParseSelectStatement(List<AstNode>? ctes)
    {
        Expect(TokenType.Keyword, "SELECT");
        // A comment sitting just before SELECT (e.g. inside "( --note\n select ...)") was
        // skipped by Expect; keep it as a leading comment rendered above the select.
        var leadingComments = DrainPendingComments();
        // SELECT [DISTINCT] [TOP ...] — DISTINCT precedes TOP syntactically.
        bool distinct = false;
        if (Peek().IsKeyword("DISTINCT")) { distinct = true; Advance(); }
        string? topExpr = null;
        if (Peek().IsKeyword("TOP"))
        {
            Advance();
            var tb = new System.Text.StringBuilder();
            if (PeekIs(TokenType.LeftParen))
            {
                Advance();
                int depth = 1; tb.Append('(');
                while (!IsAtEnd() && depth > 0)
                {
                    var t = Peek();
                    if (t.Type == TokenType.LeftParen) depth++;
                    if (t.Type == TokenType.RightParen) { depth--; }
                    tb.Append(t.Value);
                    Advance();
                }
            }
            else
            {
                tb.Append(Advance().Value); // TOP n
            }
            if (Peek().Value.Equals("PERCENT", System.StringComparison.OrdinalIgnoreCase)
                && Peek().Type != TokenType.StringLiteral) { Advance(); tb.Append(" percent"); }
            // WITH TIES — TIES may lex as an identifier, so match by value.
            if (Peek().IsKeyword("WITH")
                && PeekAt(1).Value.Equals("TIES", System.StringComparison.OrdinalIgnoreCase))
            { Advance(); Advance(); tb.Append(" with ties"); }
            topExpr = tb.ToString();
        }
        var node = new SelectStatementNode { IsDistinct = distinct, TopExpr = topExpr };
        node.LeadingComments.AddRange(leadingComments);
        if (ctes != null) node.CteDefinitions.AddRange(ctes);
        node.Columns.AddRange(ParseSelectColumns());
        // SELECT ... INTO #tbl / ##global / schema.table / [quoted]
        if (Peek().IsKeyword("INTO"))
        {
            Advance();
            var nameSb = new System.Text.StringBuilder();
            nameSb.Append(Advance().Value);
            while (PeekIs(TokenType.Dot)) { nameSb.Append(Advance().Value); nameSb.Append(Advance().Value); }
            node.IntoTable = nameSb.ToString();
        }
        if (Peek().IsKeyword("FROM"))
        {
            Advance();
            var fromTable = ParseTableRef();
            node.FromClauses.Add(fromTable);
            // A -- comment on the SAME line as the FROM table stays attached to that line
            // (rather than migrating to a PostFromComment or the following statement).
            var fromTrailing = TryTakeSameLineComment();
            if (fromTrailing != null) fromTable.TrailingComment = fromTrailing;
            // Collect joins, tolerating standalone comments between FROM and each JOIN.
            while (true)
            {
                var pending = CollectStandaloneComments();
                if (IsJoinKeyword())
                {
                    var join = ParseJoin();
                    join.LeadingComments.AddRange(pending);
                    node.FromClauses.Add(join);
                }
                else
                {
                    // Not a join — push the comments onto the next construct instead.
                    node.PostFromComments.AddRange(pending);
                    break;
                }
            }
        }
        if (Peek().IsKeyword("WHERE"))  { Advance(); node.WhereConditions.AddRange(ParseConditionList(isJoinOn: false)); }
        if (Peek().IsKeyword("GROUP"))  { Advance(); Expect(TokenType.Keyword, "BY"); node.GroupByColumns.AddRange(ParseExpressionList()); }
        if (Peek().IsKeyword("HAVING")) { Advance(); node.WhereConditions.Add(new ConditionNode { LogicalOp = "having", Expression = ParseExpression() }); }
        if (Peek().IsKeyword("ORDER"))  { Advance(); Expect(TokenType.Keyword, "BY"); node.OrderByColumns.AddRange(ParseOrderByList()); }
        return node;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  INSERT
    // ═══════════════════════════════════════════════════════════════════════════

    private InsertNode ParseInsert()
    {
        Advance(); // INSERT
        if (Peek().IsKeyword("INTO")) Advance(); // optional INTO

        var table = ParseTableRef();

        // Optional column list: INSERT INTO t (col1, col2)
        var columns = new List<AstNode>();
        if (PeekIs(TokenType.LeftParen))
        {
            // Peek ahead: if first non-paren token is SELECT → this is not a column list
            int savedPos = _pos;
            Advance(); // (
            if (!Peek().IsKeyword("SELECT"))
            {
                while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
                {
                    columns.Add(ParsePrimary());
                    if (PeekIs(TokenType.Comma)) Advance();
                }
                Expect(TokenType.RightParen);
            }
            else
            {
                // Restore: the ( belongs to the subquery handled below
                _pos = savedPos;
            }
        }

        AstNode? source = null;
        if (Peek().IsKeyword("VALUES"))
        {
            source = ParseValues();
        }
        else if (Peek().IsKeyword("SELECT"))
        {
            source = ParseSelectOrSet(null);
        }
        else if (PeekIs(TokenType.LeftParen))
        {
            // (SELECT ...)
            Advance();
            source = ParseSelectOrSet(null);
            Expect(TokenType.RightParen);
        }

        return new InsertNode { Table = table, Source = source }.Tap(n => n.Columns.AddRange(columns));
    }

    private ValuesNode ParseValues()
    {
        Advance(); // VALUES
        var valuesNode = new ValuesNode();
        do
        {
            Expect(TokenType.LeftParen);
            var row = new List<AstNode>();
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
            {
                row.Add(ParseExpression());
                if (PeekIs(TokenType.Comma)) Advance();
            }
            Expect(TokenType.RightParen);
            valuesNode.Rows.Add(row);
        } while (PeekIs(TokenType.Comma) && AdvanceAndTrue());
        return valuesNode;
    }

    private bool AdvanceAndTrue() { Advance(); return true; }

    /// <summary>Consumes the comma between DECLARE variables; a comment right after the comma
    /// is attached to the variable just parsed (if it doesn't already carry one).</summary>
    private bool ConsumeCommaThenMaybeComment(DeclareNode node)
    {
        Advance(); // comma
        if (PeekIs(TokenType.LineComment))
        {
            var c = Advance().Value;
            if (node.Variables.Count > 0 && node.Variables[^1].TrailingComment == null)
                node.Variables[^1].TrailingComment = c;
        }
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  UPDATE
    // ═══════════════════════════════════════════════════════════════════════════

    private UpdateNode ParseUpdate()
    {
        Advance(); // UPDATE
        var table = ParseTableRef();
        Expect(TokenType.Keyword, "SET");

        var assignments = new List<AssignmentNode>();
        do
        {
            var target = ParsePrimary();
            Expect(TokenType.Equals);
            var value = ParseExpression();
            assignments.Add(new AssignmentNode { Target = target, Value = value });
        } while (PeekIs(TokenType.Comma) && AdvanceAndTrue());

        var fromClauses = new List<AstNode>();
        if (Peek().IsKeyword("FROM"))
        {
            Advance();
            fromClauses.Add(ParseTableRef());
            while (IsJoinKeyword()) fromClauses.Add(ParseJoin());
        }

        var whereConds = new List<AstNode>();
        if (Peek().IsKeyword("WHERE")) { Advance(); whereConds.AddRange(ParseConditionList(isJoinOn: false)); }

        return new UpdateNode { Table = table }.Tap(n =>
        {
            n.Assignments.AddRange(assignments);
            n.FromClauses.AddRange(fromClauses);
            n.WhereConditions.AddRange(whereConds);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DELETE
    // ═══════════════════════════════════════════════════════════════════════════

    private DeleteNode ParseDelete()
    {
        Advance(); // DELETE

        // Two forms:
        //   DELETE alias FROM tbl ...
        //   DELETE FROM tbl ...
        TableRefNode? targetAlias = null;
        TableRefNode? table = null;
        var fromClauses = new List<AstNode>();

        if (Peek().IsKeyword("FROM"))
        {
            // DELETE FROM tbl ...
            Advance();
            table = ParseTableRef();
            while (IsJoinKeyword()) fromClauses.Add(ParseJoin());
        }
        else
        {
            // DELETE alias FROM tbl ...
            targetAlias = ParseTableRef();
            if (Peek().IsKeyword("FROM"))
            {
                Advance();
                fromClauses.Add(ParseTableRef());
                while (IsJoinKeyword()) fromClauses.Add(ParseJoin());
            }
        }

        var whereConds = new List<AstNode>();
        if (Peek().IsKeyword("WHERE")) { Advance(); whereConds.AddRange(ParseConditionList(isJoinOn: false)); }

        return new DeleteNode { TargetAlias = targetAlias, Table = table }.Tap(n =>
        {
            n.FromClauses.AddRange(fromClauses);
            n.WhereConditions.AddRange(whereConds);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SELECT column list
    // ═══════════════════════════════════════════════════════════════════════════

    private List<SelectColumnNode> ParseSelectColumns()
    {
        var cols = new List<SelectColumnNode>();
        while (!IsAtEnd() && !IsSelectClauseKeyword())
        {
            if (PeekIs(TokenType.Comma)) { Advance(); continue; }

            // Leading comments before a column: block comments (/* */, kept inline) and line
            // comments standing on their own line (-- , rendered above the column). Capturing
            // line comments here keeps them out of ParsePrimary, where they would otherwise be
            // mis-parsed as an expression/alias.
            var leadingComments = new List<string>();
            while (PeekIs(TokenType.BlockComment) || PeekIs(TokenType.LineComment))
                leadingComments.Add(Advance().Value);

            // Rule: detect T-SQL assignment alias: [Alias] = expression
            // Pattern: simple identifier/quoted-identifier immediately followed by =
            Token? assignAlias = null;
            if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier
                && PeekAt(1).Type == TokenType.Equals)
            {
                assignAlias = Advance(); // consume alias token
                Advance();              // consume =
            }

            var expr = ParseExpression();
            Token? alias = assignAlias;
            if (alias == null)
            {
                if (Peek().IsKeyword("AS")) { Advance(); alias = Advance(); }
                else if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier
                         && !IsClauseKeyword(Peek()) && !IsGoKeyword())
                    alias = Advance();
            }
            string? comment = null;
            // Only a comment on the SAME line is a trailing comment for this column.
            comment = TryTakeSameLineComment();
            if (PeekIs(TokenType.Comma)) Advance();
            // A trailing comment may also sit AFTER the comma on the same line: "a, --note"
            // or "a, /* note */". A block comment there is transparent — glued to this column.
            if (comment == null) comment = TryTakeSameLineInlineComment();
            cols.Add(new SelectColumnNode { Expression = expr, Alias = alias, TrailingComment = comment }
                .Tap(c => c.LeadingComments.AddRange(leadingComments)));
        }
        return cols;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FROM / JOIN
    // ═══════════════════════════════════════════════════════════════════════════

    private TableRefNode ParseTableRef()
    {
        // Subquery as table source: (SELECT ...) AS alias
        if (PeekIs(TokenType.LeftParen))
        {
            Advance(); // (
            var sub = ParseSelectOrSet(null);
            Expect(TokenType.RightParen);
            Token? sqAlias = null;
            if (Peek().IsKeyword("AS")) { Advance(); sqAlias = Advance(); }
            else if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier && !IsJoinKeyword() && !IsSelectClauseKeyword())
                sqAlias = Advance();
            return new TableRefNode { SubQuery = new SubQueryNode { Select = sub }, Alias = sqAlias };
        }

        var nameParts = new List<Token>();
        nameParts.Add(Advance());
        while (PeekIs(TokenType.Dot)) { nameParts.Add(Advance()); nameParts.Add(Advance()); }

        // Function-valued table source: name(arg1, arg2, ...) — e.g. openjson(col) or STRING_SPLIT(col, ',')
        List<AstNode>? funcArgs = null;
        if (PeekIs(TokenType.LeftParen))
        {
            Advance(); // (
            funcArgs = new List<AstNode>();
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
            {
                funcArgs.Add(ParseExpression());
                if (PeekIs(TokenType.Comma)) Advance();
                else break;
            }
            Expect(TokenType.RightParen);
        }

        Token? alias = null;
        if (Peek().IsKeyword("AS")) { Advance(); alias = Advance(); }
        else if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier
                 && !IsJoinKeyword() && !IsSelectClauseKeyword() && !IsGoKeyword())
            alias = Advance();

        // Table hint: WITH (NOLOCK), WITH (INDEX(..)), etc. — capture raw hint text.
        string? hint = null;
        if (Peek().IsKeyword("WITH") && PeekAt(1).Type == TokenType.LeftParen)
        {
            Advance(); // WITH
            Advance(); // (
            var hb = new System.Text.StringBuilder();
            int depth = 1;
            while (!IsAtEnd() && depth > 0)
            {
                var t = Peek();
                if (t.Type == TokenType.LeftParen)  depth++;
                if (t.Type == TokenType.RightParen) { depth--; if (depth == 0) { Advance(); break; } }
                if (hb.Length > 0 && t.Type != TokenType.Comma
                    && !(hb.Length > 0 && hb[hb.Length-1] == '(')
                    && t.Type != TokenType.RightParen && t.Type != TokenType.LeftParen)
                    hb.Append(' ');
                if (t.Type == TokenType.Comma) hb.Append(", ");
                else hb.Append(t.Value);
                Advance();
            }
            hint = hb.ToString().Trim();
        }

        bool isOpenQuery = nameParts.Count == 1
            && nameParts[0].Value.Equals("openquery", System.StringComparison.OrdinalIgnoreCase)
            && funcArgs != null;
        return new TableRefNode { Alias = alias, FuncArgs = funcArgs, IsOpenQuery = isOpenQuery, HintNolock = hint }
            .Tap(n => n.Name.AddRange(nameParts));
    }

    private bool IsJoinKeyword()
    {
        var t = Peek();
        return t.IsKeyword("INNER") || t.IsKeyword("LEFT") || t.IsKeyword("RIGHT")
            || t.IsKeyword("FULL") || t.IsKeyword("CROSS") || t.IsKeyword("JOIN")
            || t.IsKeyword("OUTER") // removed for LEFT/RIGHT/FULL OUTER JOIN
            || t.IsKeyword("APPLY"); // CROSS APPLY / OUTER APPLY
    }

    private JoinNode ParseJoin()
    {
        var parts = new System.Text.StringBuilder();
        while (IsJoinKeyword()) { if (parts.Length > 0) parts.Append(' '); parts.Append(Advance().Value.ToLowerInvariant()); }
        // Rule: remove OUTER only when it precedes JOIN (not APPLY)
        var jt = System.Text.RegularExpressions.Regex.Replace(parts.ToString(), @"\bouter\s+join\b", "join");
        jt = System.Text.RegularExpressions.Regex.Replace(jt, @"\s+", " ").Trim();
        if (jt == "join") jt = "inner join";
        parts.Clear(); parts.Append(jt);
        var table = ParseTableRef();
        var conditions = new List<AstNode>();
        if (Peek().IsKeyword("ON")) { Advance(); conditions.AddRange(ParseConditionList(isJoinOn: true)); }
        return new JoinNode { JoinType = parts.ToString(), Table = table }.Tap(n => n.Conditions.AddRange(conditions));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WHERE / condition lists
    // ═══════════════════════════════════════════════════════════════════════════

    private List<AstNode> ParseConditionList(bool isJoinOn)
    {
        var list = new List<AstNode>();
        string? logicalOp = null;
        while (!IsAtEnd() && !IsConditionTerminator(isJoinOn))
        {
            if (Peek().IsKeyword("AND")) { logicalOp = "and"; Advance(); continue; }
            if (Peek().IsKeyword("OR"))  { logicalOp = "or";  Advance(); continue; }
            var expr = ParseExpression();
            // Inline comment on the same line (block or line) trails this condition.
            string? condComment = TryTakeSameLineInlineComment();
            list.Add(new ConditionNode { LogicalOp = string.IsNullOrEmpty(logicalOp) ? null : logicalOp, Expression = expr, TrailingComment = condComment });
            logicalOp = null;
        }
        return list;
    }

    private bool IsConditionTerminator(bool isJoinOn)
    {
        var t = Peek();
        bool common = t.IsKeyword("GROUP") || t.IsKeyword("ORDER") || t.IsKeyword("HAVING")
            || t.IsKeyword("UNION") || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT")
            || t.IsKeyword("END")    // terminates inside BEGIN blocks
            || t.IsKeyword("SELECT") || t.IsKeyword("INSERT") || t.IsKeyword("UPDATE")
            || t.IsKeyword("DELETE") || t.IsKeyword("CREATE") || t.IsKeyword("DROP")
            || t.IsKeyword("BEGIN")  || t.Type == TokenType.DeclareKeyword
            || t.Type == TokenType.EndOfFile || t.Type == TokenType.RightParen
            || t.Type == TokenType.Semicolon || IsGoKeyword();
        if (isJoinOn) return common || t.IsKeyword("WHERE") || IsJoinKeyword();
        return common;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Expressions
    // ═══════════════════════════════════════════════════════════════════════════

    private AstNode ParseExpression() => ParseComparison();

    private AstNode ParseComparison()
    {
        var left = ParseAdditive();
        var op = Peek();
        if (op.Type is TokenType.Equals or TokenType.NotEquals or TokenType.LessThan
            or TokenType.GreaterThan or TokenType.LessOrEqual or TokenType.GreaterOrEqual)
        { Advance(); return new BinaryExprNode { Left = left, Op = op, Right = ParseAdditive() }; }
        if (op.IsKeyword("IS"))
        {
            Advance(); bool notNull = false;
            if (Peek().IsKeyword("NOT")) { notNull = true; Advance(); }
            Expect(TokenType.Keyword, "NULL");
            return new IsNullExprNode { Left = left, IsNotNull = notNull };
        }
        if (op.IsKeyword("NOT"))
        {
            var next = PeekAt(1);
            if (next.IsKeyword("IN"))   { Advance(); Advance(); Expect(TokenType.LeftParen);
                if (PeekPastComments().IsKeyword("SELECT")) { var sub = ParseSelectOrSet(null); Expect(TokenType.RightParen); return new InExprNode { Left = left, Negated = true, SubQuery = new SubQueryNode { Select = sub } }; }
                var v = ParseInList(); Expect(TokenType.RightParen); return new InExprNode { Left = left, Negated = true }.Tap(n => n.Values.AddRange(v)); }
            if (next.IsKeyword("LIKE")) { Advance(); Advance(); return new LikeExprNode { Left = left, Pattern = ParseAdditive() }; }
            if (next.IsKeyword("BETWEEN")) { Advance(); Advance(); var lo = ParseAdditive(); Expect(TokenType.Keyword, "AND"); var hi = ParseAdditive(); return new BetweenExprNode { Left = left, Low = lo, High = hi }; }
        }
        if (op.IsKeyword("IN"))     { Advance(); Expect(TokenType.LeftParen);
            if (PeekPastComments().IsKeyword("SELECT")) { var sub = ParseSelectOrSet(null); Expect(TokenType.RightParen); return new InExprNode { Left = left, SubQuery = new SubQueryNode { Select = sub } }; }
            var v = ParseInList(); Expect(TokenType.RightParen); return new InExprNode { Left = left }.Tap(n => n.Values.AddRange(v)); }
        if (op.IsKeyword("LIKE"))   { Advance(); return new LikeExprNode { Left = left, Pattern = ParseAdditive() }; }
        if (op.IsKeyword("BETWEEN")){ Advance(); var lo = ParseAdditive(); Expect(TokenType.Keyword, "AND"); var hi = ParseAdditive(); return new BetweenExprNode { Left = left, Low = lo, High = hi }; }
        return left;
    }

    /// <summary>Handles +, -, string concatenation.</summary>
    private AstNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            var op = Peek();
            if (op.Type is not (TokenType.Plus or TokenType.Minus)) break;
            // Don't consume Minus that could start a negative number literal in a list context
            Advance();
            var right = ParseMultiplicative();
            left = new BinaryExprNode { Left = left, Op = op, Right = right };
        }
        return left;
    }

    private AstNode ParseMultiplicative()
    {
        var left = ParsePrimary();
        left = ApplyCollate(left);
        while (true)
        {
            var op = Peek();
            if (op.Type is not (TokenType.Multiply or TokenType.Divide or TokenType.Percent)) break;
            Advance();
            var right = ApplyCollate(ParsePrimary());
            left = new BinaryExprNode { Left = left, Op = op, Right = right };
        }
        return left;
    }

    /// <summary>Applies a trailing COLLATE clause to an expression: expr COLLATE Collation_Name.</summary>
    private AstNode ApplyCollate(AstNode expr)
    {
        if (Peek().IsKeyword("COLLATE"))
        {
            var op = Advance();                 // COLLATE
            var collation = Advance();           // collation name (identifier)
            return new BinaryExprNode { Left = expr, Op = op, Right = new ColumnRefNode().Tap(c => c.Parts.Add(collation)) };
        }
        return expr;
    }

    private AstNode ParsePrimary()
    {
        var tok = Peek();

        // Parenthesised expression or subquery
        if (tok.Type == TokenType.LeftParen)
        {
            Advance();
            if (Peek().IsKeyword("SELECT"))
            {
                var sub = ParseSelectOrSet(null);
                Expect(TokenType.RightParen);
                return new SubQueryNode { Select = sub };
            }
            var inner = ParseExpression();
            // If a boolean operator follows, this is a condition group: ( a = 1 or b = 2 )
            if (Peek().IsKeyword("AND") || Peek().IsKeyword("OR"))
            {
                var group = new ConditionGroupNode();
                group.Conditions.Add(new ConditionNode { Expression = inner });
                while (Peek().IsKeyword("AND") || Peek().IsKeyword("OR"))
                {
                    string op = Peek().IsKeyword("AND") ? "and" : "or";
                    Advance();
                    var next = ParseExpression();
                    group.Conditions.Add(new ConditionNode { LogicalOp = op, Expression = next });
                }
                Expect(TokenType.RightParen);
                return group;
            }
            Expect(TokenType.RightParen);
            return new ParenExprNode { Inner = inner };
        }

        if (tok.IsKeyword("CASE")) return ParseCase();
        if (IsCastFunction(tok))   return ParseCastFunction();

        // EXISTS (subquery) — must come before general function-call detection
        // because exists( would otherwise be intercepted by ParseFunctionCall()
        if (tok.IsKeyword("EXISTS"))
        {
            Advance(); // exists
            Expect(TokenType.LeftParen);
            var sub = ParseSelectOrSet(null);
            Expect(TokenType.RightParen);
            return new FunctionCallNode { Name = "EXISTS", IsKeywordFunction = true }
                .Tap(n => n.Arguments.Add(new SubQueryNode { Select = sub }));
        }

        // Function call: identifier/keyword followed by (
        if (tok.Type is TokenType.Identifier or TokenType.Keyword)
            if (PeekAt(1).Type == TokenType.LeftParen) return ParseFunctionCall();

        // Column reference / identifier chain
        if (tok.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Variable or TokenType.Keyword)
        {
            var col = new ColumnRefNode();
            col.Parts.Add(Advance());
            while (PeekIs(TokenType.Dot)) { col.Parts.Add(Advance()); col.Parts.Add(Advance()); }
            // Dotted / quoted function call: schema.fn(args), [db].[schema].[fn](args).
            // (A single-part name followed by '(' was already handled as a function above.)
            if (PeekIs(TokenType.LeftParen) && tok.Type != TokenType.Variable)
            {
                var fnName = string.Join("", col.Parts.Select(p => p.Value));
                return ParseFunctionCallBody(fnName, isKeyword: false);
            }
            col.TrailingComment = TryTakeSameLineComment();
            return col;
        }

        // Unary minus: -1 → negative literal; -(expr) → BinaryExpr
        if (tok.Type == TokenType.Minus)
        {
            Advance();
            var next = Peek();
            if (next.Type == TokenType.NumberLiteral)
                return new LiteralNode { Token = new Token(TokenType.NumberLiteral, "-" + Advance().Value) };
            var operand = ParsePrimary();
            return new BinaryExprNode
            {
                Left  = new LiteralNode { Token = new Token(TokenType.NumberLiteral, "0") },
                Op    = tok,
                Right = operand
            };
        }

        // Literals
        if (tok.Type is TokenType.StringLiteral or TokenType.NumberLiteral)
        { var lit = new LiteralNode { Token = Advance() }; lit.TrailingComment = TryTakeSameLineComment(); return lit; }
        if (tok.Type == TokenType.BlockComment) return new LiteralNode { Token = Advance() };

        // A -- line comment is never an operand: turning it into a "value" would swallow the
        // rest of the line (real columns/conditions). Signal a parse error so the caller can
        // recover (callers that expect leading comments capture them before reaching here).
        if (tok.Type == TokenType.LineComment)
            throw new ParseException($"Line comment cannot be an expression at line {tok.Line}, col {tok.Column}: '{tok.Value}'.");

        // Wildcard SELECT *
        if (tok.Type == TokenType.Multiply)
        { return new LiteralNode { Token = Advance() }; }

        return new LiteralNode { Token = Advance() };
    }

    private bool IsCastFunction(Token t) =>
        t.Value.Equals("CAST", StringComparison.OrdinalIgnoreCase) ||
        t.Value.Equals("TRY_CAST", StringComparison.OrdinalIgnoreCase) ||
        t.Value.Equals("CONVERT", StringComparison.OrdinalIgnoreCase) ||
        t.Value.Equals("TRY_CONVERT", StringComparison.OrdinalIgnoreCase);

    private FunctionCallNode ParseCastFunction()
    {
        var nameToken = Advance();
        Expect(TokenType.LeftParen);
        var args = new List<AstNode>();
        bool isConvert = nameToken.Value.Equals("CONVERT", StringComparison.OrdinalIgnoreCase)
                      || nameToken.Value.Equals("TRY_CONVERT", StringComparison.OrdinalIgnoreCase);
        if (isConvert)
        {
            args.Add(ParseDataType());
            if (PeekIs(TokenType.Comma)) Advance();
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen)) { args.Add(ParseExpression()); if (PeekIs(TokenType.Comma)) Advance(); else break; }
        }
        else
        {
            args.Add(ParseExpression());
            if (Peek().IsKeyword("AS")) Advance();
            args.Add(ParseDataType());
        }
        Expect(TokenType.RightParen);
        return new FunctionCallNode { Name = nameToken.Value, IsKeywordFunction = nameToken.Type == TokenType.Keyword }.Tap(n => n.Arguments.AddRange(args));
    }

    private AstNode ParseDataType()
    {
        // Keep every token, INCLUDING the parens and length/precision, so the type renders
        // as "varchar(10)" / "decimal(18, 2)" instead of losing its parens ("varchar10").
        var raw = new RawTokensNode();
        raw.Tokens.Add(Advance());                 // type name
        if (PeekIs(TokenType.LeftParen))
        {
            raw.Tokens.Add(Advance());             // (
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
            {
                raw.Tokens.Add(Advance());
                if (PeekIs(TokenType.Comma)) raw.Tokens.Add(Advance());
            }
            if (PeekIs(TokenType.RightParen)) raw.Tokens.Add(Advance());   // )
        }
        return raw;
    }

    /// <summary>
    /// Parses an IN value list. /* */ block comments are transparent — each glues to the
    /// preceding value and the list stays inline. A -- line comment attaches to the preceding
    /// value as a CommentedValueNode, which breaks the list onto separate lines (rule 2.3.2).
    /// </summary>
    private List<AstNode> ParseInList()
    {
        var list = new List<AstNode>();
        while (!IsAtEnd())
        {
            while (_pos < _tokens.Count && _tokens[_pos].Type is TokenType.Whitespace or TokenType.Newline)
                _pos++;
            if (_pos >= _tokens.Count) break;
            var t = _tokens[_pos].Type;
            if (t == TokenType.RightParen) break;
            if (t == TokenType.Comma) { _pos++; continue; }
            // A /* */ block comment between values is transparent — glue it to the preceding value.
            if (t == TokenType.BlockComment) { GlueInListBlockComment(list, _tokens[_pos++].Value); continue; }
            // A -- line comment between values attaches to the preceding value (breaks the list).
            if (t == TokenType.LineComment) { PromoteLastToCommented(list, _tokens[_pos++].Value); continue; }

            // Parse a value.
            var val = ParsePrimary();
            // ParsePrimary may have captured a same-line -- comment on the value itself.
            var lineComment = TakeLineCommentFrom(val);

            // A /* */ block comment glued right after the value stays attached to it, inline.
            while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace) _pos++;
            while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.BlockComment)
            {
                AttachGluedBlockComment(val, _tokens[_pos++].Value);
                while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace) _pos++;
            }

            list.Add(lineComment != null
                ? new CommentedValueNode { Value = val, TrailingComment = lineComment }
                : val);
        }
        return list;
    }

    /// <summary>Glues a transparent /* */ block comment onto the last parsed IN value.</summary>
    private static void GlueInListBlockComment(List<AstNode> list, string blockComment)
    {
        if (list.Count == 0) return;   // leading comment with no preceding value (rare) — dropped
        AttachGluedBlockComment(list[^1] is CommentedValueNode cv ? cv.Value : list[^1], blockComment);
    }

    /// <summary>Appends a glued /* */ block comment to a value node that can carry a trailing comment.</summary>
    private static void AttachGluedBlockComment(AstNode val, string blockComment)
    {
        switch (val)
        {
            case LiteralNode l:   l.TrailingComment = (l.TrailingComment ?? "") + blockComment; break;
            case ColumnRefNode c: c.TrailingComment = (c.TrailingComment ?? "") + blockComment; break;
            // other value kinds have no comment slot — the transparent comment is dropped (rare)
        }
    }

    /// <summary>Extracts and clears a -- line comment captured on a value node (block comments stay).</summary>
    private static string? TakeLineCommentFrom(AstNode val)
    {
        if (val is LiteralNode l && l.TrailingComment != null && l.TrailingComment.StartsWith("--"))
        { var s = l.TrailingComment; l.TrailingComment = null; return s; }
        if (val is ColumnRefNode c && c.TrailingComment != null && c.TrailingComment.StartsWith("--"))
        { var s = c.TrailingComment; c.TrailingComment = null; return s; }
        return null;
    }

    /// <summary>Attaches a -- line comment to the last IN value, wrapping it in a CommentedValueNode.</summary>
    private static void PromoteLastToCommented(List<AstNode> list, string lineComment)
    {
        if (list.Count == 0) return;
        list[^1] = list[^1] is CommentedValueNode cv
            ? new CommentedValueNode { Value = cv.Value, TrailingComment = (cv.TrailingComment ?? "") + " " + lineComment }
            : new CommentedValueNode { Value = list[^1], TrailingComment = lineComment };
    }

    private CaseExprNode ParseCase()
    {
        Expect(TokenType.Keyword, "CASE");
        AstNode? inputExpr = null;
        if (!Peek().IsKeyword("WHEN")) inputExpr = ParsePrimary();
        var whens = new List<WhenClauseNode>();
        AstNode? elseExpr = null; string? elseComment = null;
        while (Peek().IsKeyword("WHEN"))
        {
            Advance();
            var conds = new List<AstNode>(); conds.Add(ParseExpression());
            while (Peek().IsKeyword("AND")) { Advance(); conds.Add(ParseExpression()); }
            Expect(TokenType.Keyword, "THEN");
            var then = ParseExpression(); string? tc = null;
            if (PeekIs(TokenType.LineComment)) tc = Advance().Value;
            whens.Add(new WhenClauseNode { Then = then, ThenComment = tc }.Tap(w => w.Conditions.AddRange(conds)));
        }
        if (Peek().IsKeyword("ELSE")) { Advance(); elseExpr = ParseExpression(); if (PeekIs(TokenType.LineComment)) elseComment = Advance().Value; }
        Expect(TokenType.Keyword, "END");
        return new CaseExprNode { InputExpr = inputExpr, ElseExpr = elseExpr, ElseComment = elseComment }.Tap(n => n.WhenClauses.AddRange(whens));
    }

    private FunctionCallNode ParseFunctionCall()
    {
        var nameToken = Advance(); // name (single token; '(' is next)
        return ParseFunctionCallBody(nameToken.Value, nameToken.Type == TokenType.Keyword);
    }

    /// <summary>
    /// Parses a function call's '(' argument list ')' and optional OVER clause. The name may
    /// be multi-part (e.g. schema.fn) and is supplied by the caller; the current token is '('.
    /// </summary>
    private FunctionCallNode ParseFunctionCallBody(string name, bool isKeyword)
    {
        Advance(); // (
        // Optional set quantifier in aggregates: count(distinct x), sum(all y)
        string? setQuantifier = null;
        if (Peek().IsKeyword("DISTINCT")) { Advance(); setQuantifier = "distinct"; }
        else if (Peek().IsKeyword("ALL")) { Advance(); setQuantifier = "all"; }
        var args = new List<AstNode>();
        while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
        {
            // Handle OVER (...) for window functions
            if (Peek().IsKeyword("OVER")) { Advance(); args.Add(ParseWindowSpec()); continue; }
            args.Add(ParseExpression());
            if (PeekIs(TokenType.Comma)) Advance(); else break;
        }
        Expect(TokenType.RightParen);
        // Window function: OVER clause may follow the closing paren
        AstNode? overClause = null;
        if (Peek().IsKeyword("OVER")) { Advance(); overClause = ParseWindowSpec(); }
        var fn = new FunctionCallNode { Name = name, IsKeywordFunction = isKeyword, OverClause = overClause, SetQuantifier = setQuantifier };
        fn.Arguments.AddRange(args);
        return fn;
    }

    private AstNode ParseWindowSpec()
    {
        // OVER (PARTITION BY ... ORDER BY ...)  — emit as raw tokens for now
        Expect(TokenType.LeftParen);
        var raw = new RawTokensNode();
        int depth = 1;
        while (!IsAtEnd() && depth > 0)
        {
            var t = PeekRaw();
            if (t.Type == TokenType.LeftParen)  depth++;
            if (t.Type == TokenType.RightParen) { depth--; if (depth == 0) { AdvanceRaw(); break; } }
            raw.Tokens.Add(AdvanceRaw());
        }
        return raw;
    }

    private List<AstNode> ParseExpressionList()
    {
        var list = new List<AstNode>();
        while (!IsAtEnd() && !IsSelectClauseKeyword())
        {
            list.Add(ParseExpression());
            if (Peek().IsKeyword("ASC") || Peek().IsKeyword("DESC")) Advance();
            if (PeekIs(TokenType.Comma)) { Advance(); continue; } break;
        }
        return list;
    }

    /// <summary>Parses ORDER BY items, preserving optional ASC/DESC direction.</summary>
    private List<AstNode> ParseOrderByList()
    {
        var list = new List<AstNode>();
        while (!IsAtEnd() && !IsSelectClauseKeyword())
        {
            var expr = ParseExpression();
            string? dir = null;
            if (Peek().IsKeyword("ASC"))  { Advance(); dir = "asc";  }
            else if (Peek().IsKeyword("DESC")) { Advance(); dir = "desc"; }
            list.Add(new OrderByItemNode { Expression = expr, Direction = dir });
            if (PeekIs(TokenType.Comma)) { Advance(); continue; } break;
        }
        return list;
    }


    // ═══════════════════════════════════════════════════════════════════════════
    //  BEGIN / END  (rule 2.8)
    // ═══════════════════════════════════════════════════════════════════════════

    private BeginEndNode ParseBeginEnd()
    {
        Advance(); // BEGIN
        var node = new BeginEndNode();
        while (!IsAtEnd() && !Peek().IsKeyword("END"))
        {
            var stmt = ParseStatement();
            if (stmt != null) node.Body.Add(stmt);
        }
        if (Peek().IsKeyword("END")) Advance(); // END
        return node;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CREATE TABLE  (rule 2.14)
    // ═══════════════════════════════════════════════════════════════════════════

    private AstNode ParseCreate()
    {
        Advance(); // CREATE
        if (Peek().IsKeyword("TABLE")) return ParseCreateTable();
        // Unknown CREATE → raw fallback
        return ParseRawStatement();
    }

    private CreateTableNode ParseCreateTable()
    {
        Advance(); // TABLE
        // Collect table name (might be schema.table or #temp)
        var nameTokens = new System.Text.StringBuilder();
        while (!IsAtEnd() && !PeekIs(TokenType.LeftParen) && !IsGoKeyword()
               && Peek().Type != TokenType.EndOfFile)
        {
            nameTokens.Append(Advance().Value);
        }
        var tableName = nameTokens.ToString().Trim();
        var node = new CreateTableNode { TableName = tableName };

        if (!PeekIs(TokenType.LeftParen)) return node;
        Advance(); // outer (

        while (!IsAtEnd())
        {
            // Skip whitespace/newlines between column definitions
            while (!IsAtEnd() && _tokens[_pos].Type is TokenType.Whitespace or TokenType.Newline)
                _pos++;
            // Raw check: stop at outer )
            if (_pos >= _tokens.Count || _tokens[_pos].Type == TokenType.RightParen) break;

            // Column name (next non-whitespace token)
            var colName = Advance().Value;

            // Collect definition: everything until a DEPTH-0 comma or closing )
            // Track paren depth so varchar(255) and decimal(18,2) are parsed whole.
            var defTokens = new System.Text.StringBuilder();
            int depth = 0;
            while (!IsAtEnd())
            {
                var raw = _tokens[_pos];
                // Whitespace/newline: add a space only before non-paren, non-comma content
                if (raw.Type is TokenType.Whitespace or TokenType.Newline)
                {
                    if (depth == 0)
                    {
                        // peek what follows
                        int la = _pos + 1;
                        while (la < _tokens.Count && _tokens[la].Type is TokenType.Whitespace or TokenType.Newline) la++;
                        if (la < _tokens.Count && defTokens.Length > 0)
                        {
                            var nxt = _tokens[la].Type;
                            if (nxt != TokenType.LeftParen && nxt != TokenType.Comma
                                && nxt != TokenType.RightParen)
                                defTokens.Append(' ');
                        }
                    }
                    _pos++;
                    continue;
                }
                if (raw.Type == TokenType.LeftParen)  { depth++; defTokens.Append('('); _pos++; continue; }
                if (raw.Type == TokenType.RightParen)
                {
                    if (depth == 0) break;          // outer ) of CREATE TABLE
                    depth--;
                    defTokens.Append(')');
                    _pos++;
                    continue;
                }
                if (raw.Type == TokenType.Comma && depth == 0) break; // column separator
                // Regular token — no extra space before/after ( or )
                if (defTokens.Length > 0 && raw.Type == TokenType.Comma && depth > 0)
                    defTokens.Append(", ");         // "decimal(18, 2)" — space after comma in type
                else
                    // Type/constraint keywords (int, varchar, not, null, default ...) lowercased.
                    defTokens.Append(raw.Type == TokenType.Keyword ? raw.Value.ToLowerInvariant() : raw.Value);
                _pos++;
            }

            node.Columns.Add(new ColumnDefNode
            {
                Name       = colName,
                Definition = defTokens.ToString().Trim()
            });

            // Consume depth-0 comma separator between columns
            if (!IsAtEnd() && _tokens[_pos].Type == TokenType.Comma) _pos++;
        }
        // Consume outer )
        if (!IsAtEnd() && _tokens[_pos].Type == TokenType.RightParen) _pos++;
        return node;
    }

    private AstNode ParseDrop()
    {
        Advance(); // DROP
        if (Peek().IsKeyword("TABLE"))
        {
            Advance(); // TABLE
            bool ifExists = false;
            if (Peek().IsKeyword("IF")) { Advance(); Expect(TokenType.Keyword, "EXISTS"); ifExists = true; }
            var nameTokens = new System.Text.StringBuilder();
            while (!IsAtEnd() && !IsGoKeyword() && Peek().Type != TokenType.EndOfFile
                   && Peek().Type != TokenType.Semicolon
                   && !Peek().IsKeyword("CREATE") && !Peek().IsKeyword("SELECT")
                   && !Peek().IsKeyword("INSERT") && !Peek().IsKeyword("DROP")
                   && !Peek().IsKeyword("BEGIN")
                   && Peek().Type != TokenType.DeclareKeyword)
                nameTokens.Append(Advance().Value);
            return new DropTableNode { IfExists = ifExists, TableName = nameTokens.ToString().Trim() };
        }
        return ParseRawStatement();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Raw fallback
    // ═══════════════════════════════════════════════════════════════════════════

    private RawTokensNode ParseRawStatement()
    {
        var raw = new RawTokensNode();
        while (!IsAtEnd())
        {
            var t = Peek();
            if (t.IsKeyword("SELECT") || t.IsKeyword("WITH") || t.IsKeyword("INSERT")
                || t.IsKeyword("UPDATE") || t.IsKeyword("DELETE")
                || t.IsKeyword("DROP")  || t.IsKeyword("CREATE") || t.IsKeyword("BEGIN")
                || t.Type == TokenType.DeclareKeyword
                || t.Type == TokenType.EndOfFile || IsGoKeyword()) break;
            raw.Tokens.Add(Advance());
        }
        return raw;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private bool IsSelectClauseKeyword()
    {
        var t = Peek();
        return t.IsKeyword("FROM") || t.IsKeyword("WHERE") || t.IsKeyword("GROUP")
            || t.IsKeyword("ORDER") || t.IsKeyword("HAVING") || t.IsKeyword("UNION")
            || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT") || t.IsKeyword("SELECT")
            || t.IsKeyword("INTO")   // SELECT ... INTO #tbl — ends the column list
            || t.IsKeyword("END")  // terminates a column list inside BEGIN/END
            // A new statement keyword ends the column list of an assignment SELECT that has
            // no FROM, e.g. "select @x = 1 \n declare ..." or "... \n exec (...)".
            || t.IsKeyword("INSERT") || t.IsKeyword("UPDATE") || t.IsKeyword("DELETE")
            || t.IsKeyword("CREATE") || t.IsKeyword("DROP")   || t.IsKeyword("BEGIN")
            || t.IsKeyword("EXEC")   || t.IsKeyword("EXECUTE")|| t.IsKeyword("IF")
            || t.IsKeyword("WHILE")  || t.IsKeyword("RETURN") || t.IsKeyword("PRINT")
            || t.IsKeyword("MERGE")  || t.IsKeyword("TRUNCATE")
            || t.Type == TokenType.DeclareKeyword
            || t.Type == TokenType.EndOfFile || t.Type == TokenType.RightParen
            || t.Type == TokenType.Semicolon || IsGoKeyword();
    }

    private bool IsClauseKeyword(Token t) =>
        t.IsKeyword("FROM") || t.IsKeyword("WHERE") || t.IsKeyword("GROUP") || t.IsKeyword("ORDER")
        || t.IsKeyword("HAVING") || t.IsKeyword("UNION") || t.IsKeyword("INNER") || t.IsKeyword("LEFT")
        || t.IsKeyword("RIGHT") || t.IsKeyword("JOIN") || t.IsKeyword("ON") || t.IsKeyword("SELECT")
        || t.IsKeyword("WITH") || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT") || t.IsKeyword("END")
        || t.IsKeyword("INTO");

    private Token Peek()
    {
        int i = _pos;
        while (i < _tokens.Count && Skippable.Contains(_tokens[i].Type)) i++;
        return i < _tokens.Count ? _tokens[i] : new Token(TokenType.EndOfFile, string.Empty);
    }

    /// <summary>
    /// Like Peek() but also skips standalone comment tokens, so callers can detect a
    /// clause keyword (JOIN, WHERE, ...) that follows one or more comment lines.
    /// </summary>
    private Token PeekPastComments()
    {
        int i = _pos;
        while (i < _tokens.Count &&
               (Skippable.Contains(_tokens[i].Type)
                || _tokens[i].Type == TokenType.LineComment
                || _tokens[i].Type == TokenType.BlockComment)) i++;
        return i < _tokens.Count ? _tokens[i] : new Token(TokenType.EndOfFile, string.Empty);
    }

    /// <summary>Consumes and returns any standalone comments at the current position.</summary>
    private List<string> CollectStandaloneComments()
    {
        var comments = new List<string>();
        while (true)
        {
            // Look past whitespace/newlines WITHOUT consuming them, so a run with no comment
            // leaves the separators intact (blank-line detection upstream depends on it).
            int i = _pos;
            while (i < _tokens.Count && Skippable.Contains(_tokens[i].Type)) i++;
            if (i < _tokens.Count &&
                (_tokens[i].Type == TokenType.LineComment || _tokens[i].Type == TokenType.BlockComment))
            {
                comments.Add(_tokens[i].Value);
                _pos = i + 1;
            }
            else break;
        }
        return comments;
    }

    private Token PeekAt(int offset)
    {
        int count = 0, i = _pos;
        while (i < _tokens.Count)
        {
            if (!Skippable.Contains(_tokens[i].Type)) { if (count == offset) return _tokens[i]; count++; }
            i++;
        }
        return new Token(TokenType.EndOfFile, string.Empty);
    }

    /// <summary>Counts consecutive Newline tokens immediately before the next meaningful token.</summary>
    private int CountNewlinesBackFromCurrent()
    {
        int i = _pos - 1, count = 0;
        while (i >= 0 && (_tokens[i].Type == TokenType.Newline || _tokens[i].Type == TokenType.Whitespace))
        {
            if (_tokens[i].Type == TokenType.Newline) count++;
            i--;
        }
        return count;
    }

    /// <summary>Counts consecutive Newline tokens immediately after the current position.</summary>
    private int CountNewlinesForwardFromCurrent()
    {
        int i = _pos, count = 0;
        while (i < _tokens.Count && (_tokens[i].Type == TokenType.Newline || _tokens[i].Type == TokenType.Whitespace))
        {
            if (_tokens[i].Type == TokenType.Newline) count++;
            i++;
        }
        return count;
    }

    private Token PeekRaw() => _pos < _tokens.Count ? _tokens[_pos] : new Token(TokenType.EndOfFile, string.Empty);

    /// <summary>
    /// If a line comment follows on the SAME line (only whitespace between here and it,
    /// no newline), consumes and returns it. Otherwise returns null and consumes nothing.
    /// Used so a comment on its own line is treated as standalone, not as a trailing comment.
    /// </summary>
    private string? TryTakeSameLineComment()
    {
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        if (i < _tokens.Count && _tokens[i].Type == TokenType.LineComment)
        {
            var val = _tokens[i].Value;
            _pos = i + 1;
            return val;
        }
        return null;
    }

    /// <summary>
    /// Like TryTakeSameLineComment but also catches inline block comments (/* ... */) that
    /// sit on the same line, e.g. "expr /* note */ and ...". Returns null if none.
    /// </summary>
    private string? TryTakeSameLineInlineComment()
    {
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        if (i < _tokens.Count &&
            (_tokens[i].Type == TokenType.LineComment || _tokens[i].Type == TokenType.BlockComment))
        {
            var val = _tokens[i].Value;
            _pos = i + 1;
            return val;
        }
        return null;
    }
    private bool PeekIs(TokenType type) => Peek().Type == type;

    private Token Advance()
    {
        while (_pos < _tokens.Count && Skippable.Contains(_tokens[_pos].Type)) _pos++;
        return _pos < _tokens.Count ? _tokens[_pos++] : new Token(TokenType.EndOfFile, string.Empty);
    }

    private Token AdvanceRaw() => _pos < _tokens.Count ? _tokens[_pos++] : new Token(TokenType.EndOfFile, string.Empty);

    private Token Expect(TokenType type, string? value = null)
    {
        var t = Peek();
        if (Matches(t, type, value)) return Advance();
        // Tolerate comments sitting before the expected token: skip them (remembering their
        // text in the pending buffer) and retry, so a comment can't desync the stream
        // (e.g. "exists ( --note\n select ...)"). Only a genuine token mismatch is an error.
        if (SkipLeadingComments())
        {
            t = Peek();
            if (Matches(t, type, value)) return Advance();
        }
        throw new ParseException(type, value, t);
    }

    private static bool Matches(Token t, TokenType type, string? value) =>
        t.Type == type && (value == null || t.Value.Equals(value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Advances past leading whitespace/newlines and comment tokens, collecting the
    /// comment text into the pending buffer. Returns true if at least one comment was skipped.</summary>
    private bool SkipLeadingComments()
    {
        bool any = false;
        while (true)
        {
            int i = _pos;
            while (i < _tokens.Count && Skippable.Contains(_tokens[i].Type)) i++;
            if (i < _tokens.Count &&
                (_tokens[i].Type == TokenType.LineComment || _tokens[i].Type == TokenType.BlockComment))
            {
                _pendingComments.Add(_tokens[i].Value);
                _pos = i + 1;
                any = true;
            }
            else break;
        }
        return any;
    }

    /// <summary>Returns and clears any comments collected by Expr's comment-skipping.</summary>
    private List<string> DrainPendingComments()
    {
        if (_pendingComments.Count == 0) return new List<string>();
        var copy = new List<string>(_pendingComments);
        _pendingComments.Clear();
        return copy;
    }

    private bool TryConsume(TokenType type) { if (Peek().Type != type) return false; Advance(); return true; }
    private bool IsAtEnd() => Peek().Type == TokenType.EndOfFile;
}

internal static class NodeExtensions
{
    public static T Tap<T>(this T node, Action<T> action) where T : AstNode { action(node); return node; }
}

}
