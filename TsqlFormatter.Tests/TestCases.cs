using System.Collections.Generic;

namespace TsqlFormatter.Tests
{

/// <summary>
/// Golden test cases. Expected outputs reflect the agreed Code Style.
/// Use raw string-ish verbatim literals; tabs are real \t characters.
/// </summary>
public static class TestCases
{
    public static List<TestCase> All() => new List<TestCase>
    {
        // ── 1.1 keyword casing ────────────────────────────────────────────────
        new TestCase {
            Rule = "1.1", Name = "keywords lowercased, identifiers preserved",
            Input    = "SELECT Id, NAME from dbo.Users WHERE Status = 1",
            Expected = "select\n\tId,\n\tNAME\nfrom dbo.Users\nwhere\n\tStatus = 1",
        },

        // ── 2.1 select column layout ──────────────────────────────────────────
        new TestCase {
            Rule = "2.1", Name = "multiple columns, one per line",
            Input    = "select a, b, c from t",
            Expected = "select\n\ta,\n\tb,\n\tc\nfrom t",
        },
        new TestCase {
            Rule = "2.1.1", Name = "single column on its own indented line",
            Input    = "select a from t",
            Expected = "select\n\ta\nfrom t",
        },
        new TestCase {
            Rule = "2.1.1", Name = "single order by / group by item on its own line",
            Input    = "select count(*) from t group by a order by a",
            Expected = "select\n\tcount(*)\nfrom t\ngroup by\n\ta\norder by\n\ta",
        },

        // ── SELECT ... INTO #tbl (bug 1) ──────────────────────────────────────
        new TestCase {
            Rule = "into", Name = "select ... into #tmp on its own line between columns and from",
            Input    = "select a, b into #tmp from t where x = 1",
            Expected = "select\n\ta,\n\tb\ninto #tmp\nfrom t\nwhere\n\tx = 1",
        },

        // ── 2.2 join ───────────────────────────────────────────────────────────
        new TestCase {
            Rule = "2.2", Name = "inner join, on first condition on same line",
            Input    = "select t.a, u.b from t inner join u on u.id = t.id and u.x = t.y",
            Expected = "select\n\tt.a,\n\tu.b\nfrom t\n\tinner join u\n\t\ton u.id = t.id\n\t\tand u.x = t.y",
        },

        // ── 2.5 case ───────────────────────────────────────────────────────────
        new TestCase {
            Rule = "2.5", Name = "case/when/then/else/end",
            Input    = "select case when a = 1 then 'one' when a = 2 then 'two' else 'other' end as label from t",
            Expected = "select\n\tcase\n\t\twhen a = 1\n\t\tthen 'one'\n\t\twhen a = 2\n\t\tthen 'two'\n\t\telse 'other'\n\tend as label\nfrom t",
        },

        // ── 2.11 set operations ───────────────────────────────────────────────
        new TestCase {
            Rule = "2.11", Name = "union all: blank line before and after operator",
            Input    = "select a from t1 union all select a from t2",
            Expected = "select\n\ta\nfrom t1\n\nunion all\n\nselect\n\ta\nfrom t2",
        },
        new TestCase {
            Rule = "2.11", Name = "intersect / except chain: blank line around each operator",
            Input    = "select a from t1 intersect select a from t2 except select a from t3",
            Expected = "select\n\ta\nfrom t1\n\nintersect\n\nselect\n\ta\nfrom t2\n\nexcept\n\nselect\n\ta\nfrom t3",
        },

        // ── 2.12 insert ───────────────────────────────────────────────────────
        new TestCase {
            Rule = "2.12", Name = "insert into ... values",
            Input    = "insert into dbo.t (a, b, c) values (1, 2, 3)",
            Expected = "insert into dbo.t(a, b, c)\nvalues\n\t(1, 2, 3)",
        },

        // ── 5.1 alias via = → as ──────────────────────────────────────────────
        new TestCase {
            Rule = "5.1", Name = "alias = rewritten to as",
            Input    = "select [Name] = t.col1, cnt = count(*) from t",
            Expected = "select\n\tt.col1 as [Name],\n\tcount(*) as cnt\nfrom t",
        },

        // ── DECLARE ────────────────────────────────────────────────────────────
        // NOTE: expected uses varchar(50) WITHOUT spaces — rule 2.15 (function/type
        // parens). Currently the formatter emits "varchar ( 50 )"; this test should
        // FAIL until 2.15 is implemented, then pass.
        new TestCase {
            Rule = "2.15", Name = "declare: type parens have no inner spaces",
            Input    = "declare @a int, @b varchar(50)",
            Expected = "declare\n\t@a int,\n\t@b varchar(50)",
        },
        new TestCase {
            Rule = "2.15", Name = "declare: decimal(18, 2) spacing",
            Input    = "declare @m decimal(18,2)",
            Expected = "declare\n\t@m decimal(18, 2)",
        },
        new TestCase {
            Rule = "2.15", Name = "window function: space before over (",
            Input    = "select row_number() over (partition by dep order by sal desc) as rn from emp",
            Expected = "select\n\trow_number() over (partition by dep order by sal desc) as rn\nfrom emp",
        },
        new TestCase {
            Rule = "window", Name = "over: uppercase ORDER BY and dotted column lowercased/tightened",
            Input    = "select row_number() OVER (ORDER BY a.[Id]) as [Rn] from [dbo].[A] a",
            Expected = "select\n\trow_number() over (order by a.[Id]) as [Rn]\nfrom [dbo].[A] as a",
        },
        new TestCase {
            Rule = "window", Name = "over: partition by + order by desc, uppercase and dotted",
            Input    = "select sum(x) OVER (PARTITION BY a.[G] ORDER BY a.[D] DESC) as s from t",
            Expected = "select\n\tsum(x) over (partition by a.[G] order by a.[D] desc) as s\nfrom t",
        },
        new TestCase {
            Rule = "window", Name = "over: multiple partition/order columns keep comma spacing",
            Input    = "select count(*) OVER (PARTITION BY a.[G1], a.[G2] ORDER BY a.[D1], a.[D2]) as c from t",
            Expected = "select\n\tcount(*) over (partition by a.[G1], a.[G2] order by a.[D1], a.[D2]) as c\nfrom t",
        },

        // ── convert/cast keep type parens; dotted function calls; stmt boundaries ─
        new TestCase {
            Rule = "convert", Name = "convert keeps varchar(n) parens",
            Input    = "select convert(varchar(10), id) as c from t",
            Expected = "select\n\tconvert(varchar(10), id) as c\nfrom t",
        },
        new TestCase {
            Rule = "convert", Name = "cast keeps type parens",
            Input    = "select cast(x as varchar(20)) as c from t",
            Expected = "select\n\tcast(x as varchar(20)) as c\nfrom t",
        },
        new TestCase {
            Rule = "dottedfn", Name = "multi-part function name with args",
            Input    = "select webcar.dbo.datafirst(@yy, @mm) as d from t",
            Expected = "select\n\twebcar.dbo.datafirst(@yy, @mm) as d\nfrom t",
        },
        new TestCase {
            Rule = "stmtbound", Name = "assignment select column list ends at DECLARE",
            Input    = "select @yy=2026,@mm=6\ndeclare @z int",
            Expected = "select\n\t@yy = 2026,\n\t@mm = 6\ndeclare\n\t@z int",
        },
        new TestCase {
            Rule = "stmtbound", Name = "assignment select column list ends at EXEC",
            Input    = "select @x = 1\nexec('do_something')",
            Expected = "select\n\t@x = 1\nexec('do_something')",
        },
        new TestCase {
            Rule = "2.15", Name = "function call: no space before (",
            Input    = "select count(*), max(sal), substring(name, 1, 3) from emp",
            Expected = "select\n\tcount(*),\n\tmax(sal),\n\tsubstring(name, 1, 3)\nfrom emp",
        },

        // ── CROSS APPLY with subquery ─────────────────────────────────────────
        new TestCase {
            Rule = "apply", Name = "cross apply subquery",
            Input    = "select p.maker, l.* from Product as p cross apply (select * from Laptop as L where p.model = L.model) as l",
            Expected = "select\n\tp.maker,\n\tl.*\nfrom Product as p\n\tcross apply (\n\t\tselect\n\t\t\t*\n\t\tfrom Laptop as L\n\t\twhere\n\t\t\tp.model = L.model\n\t) as l",
        },

        // ── EXISTS subquery (regression for the recent bug) ───────────────────
        new TestCase {
            Rule = "exists", Name = "exists subquery inside iif",
            Input    = "select max(iif(exists (select * from t as x where x.id = w.id), 1, 0)) as has_it from w",
            Expected = "select\n\tmax(\n\t\tiif(\n\t\t\texists (\n\t\t\t\tselect\n\t\t\t\t\t*\n\t\t\t\tfrom t as x\n\t\t\t\twhere\n\t\t\t\t\tx.id = w.id\n\t\t\t),\n\t\t\t1,\n\t\t\t0\n\t\t)\n\t) as has_it\nfrom w",
        },

        // ── 4.1.2 trailing comments ───────────────────────────────────────────
        new TestCase {
            Rule = "4.1.2", Name = "column trailing comments, comma before comment",
            Input    = "select a, --первая колонка\nb --вторая\nfrom t",
            Expected = "select\n\ta,\t\t--первая колонка\n\tb\t\t--вторая\nfrom t",
        },
        new TestCase {
            Rule = "4.1.2", Name = "comma before trailing -- comment on a non-last column",
            Input    = "select a, b.c\t\t--note about c\n, d\nfrom t",
            Expected = "select\n\ta,\n\tb.c,\t\t--note about c\n\td\nfrom t",
        },
        new TestCase {
            Rule = "4.1.2", Name = "trailing comment on where",
            Input    = "select a from t where x = 1 --фильтр",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1\t\t--фильтр",
        },

        // ── bug 2: line comments in the column list stay on their own line ────
        new TestCase {
            Rule = "parse-safety", Name = "commented-out top and column stay on own line, real columns kept",
            Input    = "select --top (@top)\n w.cid as objectId,\n--  '' as comment,\n ac.title as city_title\nfrom t",
            Expected = "select\n\t--top (@top)\n\tw.cid as objectId,\n\t--  '' as comment,\n\tac.title as city_title\nfrom t",
        },

        // ── bug 0/3: comment before a subquery SELECT must not desync the parser ─
        new TestCase {
            Rule = "parse-safety", Name = "comment between ( and select renders above select, subquery intact",
            Input    = "select a from t where exists (\t--note\n select 1 from u where u.x = 1)",
            Expected = "select\n\ta\nfrom t\nwhere\n\texists (\n\t\t--note\n\t\tselect\n\t\t\t1\n\t\tfrom u\n\t\twhere\n\t\t\tu.x = 1\n\t)",
        },

        new TestCase {
            Rule = "parse-safety", Name = "comment before subquery select inside IN (...)",
            Input    = "select a from t where id in (\n\t--only active\n\tselect id from u where u.x = 1\n)",
            Expected = "select\n\ta\nfrom t\nwhere\n\tid in (\n\t\t--only active\n\t\tselect\n\t\t\tid\n\t\tfrom u\n\t\twhere\n\t\t\tu.x = 1\n\t)",
        },

        new TestCase {
            Rule = "option", Name = "OPTION (recompile) is a trailing clause, not a WHERE condition",
            Input    = "select a from t where x = 1 option (recompile)\n--next statement comment\nselect 2 from u",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1\noption(recompile)\n--next statement comment\nselect\n\t2\nfrom u",
        },

        // ── 2.6 OR grouping ───────────────────────────────────────────────────
        new TestCase {
            Rule = "2.6", Name = "atomic OR (2.6.4)",
            Input    = "select * from t where a = 1 or b = 2",
            Expected = "select\n\t*\nfrom t\nwhere\n\ta = 1\n\tor b = 2",
        },

        // ── 7. OPENQUERY (linked server) ──────────────────────────────────────
        new TestCase {
            Rule = "7", Name = "openquery basic: server + remote sql on own lines",
            Input    = "select w.* from openquery(api_ufaner_utf, 'select id, title from cabinet_user') as w",
            Expected = "select\n\tw.*\nfrom openquery(\n\tapi_ufaner_utf,\n\t'select id, title from cabinet_user'\n) as w",
        },
        new TestCase {
            Rule = "7", Name = "openquery inside join keeps indentation",
            Input    = "select a.id, w.title from local_table as a inner join openquery(srv, 'select id, title from remote') as w on w.id = a.id",
            Expected = "select\n\ta.id,\n\tw.title\nfrom local_table as a\n\tinner join openquery(\n\t\tsrv,\n\t\t'select id, title from remote'\n\t) as w\n\t\ton w.id = a.id",
        },

        // ── 2.13 dynamic SQL string literal ───────────────────────────────────
        new TestCase {
            Rule = "2.13", Name = "multiline dynamic sql reindented +1 tab, closing quote at declaring indent",
            Input    = "declare @s varchar(max) = ''\nselect @s = @s + '\n    select\n        id,\n        title\n    from '+b.dbName+'.dbo.contract'+b.suffix+'\n' from webcar.dbo.billing as b",
            Expected = "declare\n\t@s varchar(max) = ''\nselect\n\t@s = @s + '\n\t\tselect\n\t\t    id,\n\t\t    title\n\t\tfrom ' + b.dbName + '.dbo.contract' + b.suffix + '\n\t'\nfrom webcar.dbo.billing as b",
        },

        // ── 2.4 subquery in IN ────────────────────────────────────────────────
        new TestCase {
            Rule = "2.4", Name = "in (subquery)",
            Input    = "select * from t where id in (select id from u where u.x = 1)",
            Expected = "select\n\t*\nfrom t\nwhere\n\tid in (\n\t\tselect\n\t\t\tid\n\t\tfrom u\n\t\twhere\n\t\t\tu.x = 1\n\t)",
        },
        new TestCase {
            Rule = "2.4", Name = "not in (subquery)",
            Input    = "select * from t where id not in (select id from u)",
            Expected = "select\n\t*\nfrom t\nwhere\n\tid not in (\n\t\tselect\n\t\t\tid\n\t\tfrom u\n\t)",
        },

        // ── 2.2 join normalization (detailed) ─────────────────────────────────
        new TestCase {
            Rule = "2.2", Name = "left outer join -> left join",
            Input    = "select a.id from t1 as a left outer join t2 as b on b.id = a.id",
            Expected = "select\n\ta.id\nfrom t1 as a\n\tleft join t2 as b\n\t\ton b.id = a.id",
        },
        new TestCase {
            Rule = "2.2", Name = "bare join -> inner join",
            Input    = "select a.id from t1 as a join t2 as b on b.id = a.id",
            Expected = "select\n\ta.id\nfrom t1 as a\n\tinner join t2 as b\n\t\ton b.id = a.id",
        },

        // ── 2.5 nested case ───────────────────────────────────────────────────
        new TestCase {
            Rule = "2.5", Name = "nested case in then",
            Input    = "select case when a = 1 then case when b = 2 then 'x' else 'y' end else 'z' end from t",
            Expected = "select\n\tcase\n\t\twhen a = 1\n\t\tthen case\n\t\t\twhen b = 2\n\t\t\tthen 'x'\n\t\t\telse 'y'\n\t\tend\n\t\telse 'z'\n\tend\nfrom t",
        },

        // ── 2.7 update ... set ... from (detailed) ────────────────────────────
        new TestCase {
            Rule = "2.7", Name = "update set from join, set lowercased",
            Input    = "update w set w.a = t.a, w.b = t.b from t1 as w inner join t2 as t on t.id = w.id",
            Expected = "update w\nset\n\tw.a = t.a,\n\tw.b = t.b\nfrom t1 as w\n\tinner join t2 as t\n\t\ton t.id = w.id",
        },

        // ── CTE (with ... as) ─────────────────────────────────────────────────
        new TestCase {
            Rule = "cte", Name = "single cte: opening paren on as line",
            Input    = "WITH cte AS (SELECT s.[Id], s.[Val] FROM [dbo].[Src] s WHERE s.[Val] > 0)\nSELECT c.[Id] FROM cte c",
            Expected = "with cte as (\n\tselect\n\t\ts.[Id],\n\t\ts.[Val]\n\tfrom [dbo].[Src] as s\n\twhere\n\t\ts.[Val] > 0\n)\nselect\n\tc.[Id]\nfrom cte as c",
        },
        // ── leading block comment on columns ──────────────────────────────────
        new TestCase {
            Rule = "blockcmt", Name = "block comment in IN list is transparent, list stays inline",
            Input    = "select * from a where id in (1, 2, 3, 4, 5, 6/*7,8,9,0*/, 1, 2, 3, 4)",
            Expected = "select\n\t*\nfrom a\nwhere\n\tid in (1, 2, 3, 4, 5, 6/*7,8,9,0*/, 1, 2, 3, 4)",
        },
        new TestCase {
            Rule = "blockcmt", Name = "block comment between columns trails the preceding column, no tab",
            Input    = "SELECT 'SELECT a.[x] FROM t WHERE a.[y] = 1' AS [sql_text], /* [Alias] = expr, INNER JOIN, declare @a int */ c.[id]\nFROM [dbo].[C] c",
            Expected = "select\n\t'SELECT a.[x] FROM t WHERE a.[y] = 1' as [sql_text],/* [Alias] = expr, INNER JOIN, declare @a int */\n\tc.[id]\nfrom [dbo].[C] as c",
        },
        new TestCase {
            Rule = "2.8", Name = "begin/end with declare and select, no extra end column",
            Input    = "BEGIN\nDECLARE @a INT = 1, @b INT = 2\nSELECT @a, @b\nEND",
            Expected = "begin\n\n\tdeclare\n\t\t@a int = 1,\n\t\t@b int = 2\n\n\tselect\n\t\t@a,\n\t\t@b\n\nend",
        },

        // ── fragments (partial selections) ────────────────────────────────────
        new TestCase {
            Rule = "fragment", Name = "bare WHERE clause",
            Input    = "WHERE a.id = 1 AND b.x > 0",
            Expected = "where\n\ta.id = 1\n\tand b.x > 0",
        },
        new TestCase {
            Rule = "fragment", Name = "bare WHERE single condition on its own line",
            Input    = "where x = 1",
            Expected = "where\n\tx = 1",
        },
        new TestCase {
            Rule = "fragment", Name = "bare WHERE with OR grouping",
            Input    = "where a = 1 or b = 2",
            Expected = "where\n\ta = 1\n\tor b = 2",
        },
        new TestCase {
            Rule = "fragment", Name = "bare conditions without where keyword",
            Input    = "a = 1 and b = 2 and c = 3",
            Expected = "where\n\ta = 1\n\tand b = 2\n\tand c = 3",
        },
        new TestCase {
            Rule = "fragment", Name = "bare column list",
            Input    = "a.id, b.name, count(*) as cnt",
            Expected = "a.id,\nb.name,\ncount(*) as cnt",
        },
        new TestCase {
            Rule = "fragment", Name = "bare JOIN chain",
            Input    = "inner join t2 as b on b.id = a.id and b.x = 1",
            Expected = "\tinner join t2 as b\n\t\ton b.id = a.id\n\t\tand b.x = 1",
        },
        // ── newly covered scenarios ───────────────────────────────────────────
        new TestCase {
            Rule = "top", Name = "top (n) with ties",
            Input    = "select top (10) with ties a from t order by a",
            Expected = "select top (10) with ties\n\ta\nfrom t\norder by\n\ta",
        },
        new TestCase {
            Rule = "top", Name = "top n percent",
            Input    = "select top 5 percent a from t",
            Expected = "select top 5 percent\n\ta\nfrom t",
        },
        new TestCase {
            Rule = "top", Name = "distinct top n",
            Input    = "select distinct top 10 a from t",
            Expected = "select distinct top 10\n\ta\nfrom t",
        },
        new TestCase {
            Rule = "hint", Name = "with (nolock) on table",
            Input    = "select a from t with (nolock) where x = 1",
            Expected = "select\n\ta\nfrom t with (nolock)\nwhere\n\tx = 1",
        },
        new TestCase {
            Rule = "hint", Name = "with (nolock) in join",
            Input    = "select a.id from t1 as a inner join t2 as b with (nolock) on b.id = a.id",
            Expected = "select\n\ta.id\nfrom t1 as a\n\tinner join t2 as b with (nolock)\n\t\ton b.id = a.id",
        },
        new TestCase {
            Rule = "paren", Name = "nested parentheses preserved (semantics)",
            Input    = "select ((a + b) * (c - d)) as x from t",
            Expected = "select\n\t((a + b) * (c - d)) as x\nfrom t",
        },
        new TestCase {
            Rule = "orderby", Name = "order by asc/desc preserved",
            Input    = "select a from t order by a desc, b asc",
            Expected = "select\n\ta\nfrom t\norder by\n\ta desc,\n\tb asc",
        },
        new TestCase {
            Rule = "isnull", Name = "is null / is not null",
            Input    = "select a from t where b is null and c is not null",
            Expected = "select\n\ta\nfrom t\nwhere\n\tb is null\n\tand c is not null",
        },
        new TestCase {
            Rule = "derived", Name = "derived table subquery in from",
            Input    = "select x.a from (select a from t where b = 1) as x",
            Expected = "select\n\tx.a\nfrom (\n\tselect\n\t\ta\n\tfrom t\n\twhere\n\t\tb = 1\n) as x",
        },
        new TestCase {
            Rule = "simplecase", Name = "simple case (case x when)",
            Input    = "select case status when 1 then 'a' when 2 then 'b' else 'c' end from t",
            Expected = "select\n\tcase status\n\t\twhen 1\n\t\tthen 'a'\n\t\twhen 2\n\t\tthen 'b'\n\t\telse 'c'\n\tend\nfrom t",
        },

        // ── standalone comments between clauses (smoke regressions) ────────────
        new TestCase {
            Rule = "comments", Name = "standalone comment between from and join",
            Input    = "select a\nfrom t1 s\n-- note\ninner join t2 e on e.id = s.id",
            Expected = "select\n\ta\nfrom t1 as s\n\t-- note\n\tinner join t2 as e\n\t\ton e.id = s.id",
        },
        new TestCase {
            Rule = "comments", Name = "declare trailing comment after comma",
            Input    = "declare @a int = 1, --note\n@b int = 2",
            Expected = "declare\n\t@a int = 1,\t\t--note\n\t@b int = 2",
        },
        new TestCase {
            Rule = "condgroup", Name = "parenthesized condition group with and/or",
            Input    = "select a from t where x = 1 and (y = 2 or z = 3) or w = 4",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1\n\tand (\n\t\ty = 2\n\t\tor z = 3\n\t)\n\tor w = 4",
        },
        new TestCase {
            Rule = "casewt", Name = "when and then on separate lines",
            Input    = "select case when a > 1 then 'high' else 'low' end from t",
            Expected = "select\n\tcase\n\t\twhen a > 1\n\t\tthen 'high'\n\t\telse 'low'\n\tend\nfrom t",
        },
        new TestCase {
            Rule = "comments", Name = "no blank line between standalone comment and statement",
            Input    = "-- header\nselect a from t",
            Expected = "-- header\nselect\n\ta\nfrom t",
        },
        new TestCase {
            Rule = "comments", Name = "trailing -- comment stays on its original line, not moved to next statement",
            Input    = "select a from t1  -- comment about t1\nselect b from t2",
            Expected = "select\n\ta\nfrom t1\t\t-- comment about t1\nselect\n\tb\nfrom t2",
        },
        new TestCase {
            Rule = "comments", Name = "trailing -- comment on last statement kept on its line",
            Input    = "select a from t1  -- trailing note",
            Expected = "select\n\ta\nfrom t1\t\t-- trailing note",
        },
        new TestCase {
            Rule = "comments", Name = "comment after GO attaches to next statement",
            Input    = "select 1\nGO\n-- note\nselect 2 from t",
            Expected = "select\n\t1\n\nGO\n\n-- note\nselect\n\t2\nfrom t",
        },
        new TestCase {
            Rule = "go", Name = "consecutive GO separators are preserved (not collapsed)",
            Input    = "select 1\nGO\nGO\nselect 2 from t",
            Expected = "select\n\t1\n\nGO\n\nGO\n\nselect\n\t2\nfrom t",
        },
        new TestCase {
            Rule = "go", Name = "consecutive trailing GO preserved",
            Input    = "select 1 from t\nGO\nGO",
            Expected = "select\n\t1\nfrom t\n\nGO\n\nGO",
        },
        new TestCase {
            Rule = "where-or", Name = "WHERE with OR does not add outer parens",
            Input    = "select a from t where x = 1 or y = 2",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1\n\tor y = 2",
        },
        new TestCase {
            Rule = "where-or", Name = "WHERE keeps original inner group, no outer parens",
            Input    = "select a from t where x = 1 and (b = 1 or b = 2) or y > 0",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1\n\tand (\n\t\tb = 1\n\t\tor b = 2\n\t)\n\tor y > 0",
        },
        new TestCase {
            Rule = "blanklines", Name = "blank line after comment preserved",
            Input    = "-- comment\n\nselect 2 from t",
            Expected = "-- comment\n\nselect\n\t2\nfrom t",
        },
        new TestCase {
            Rule = "blanklines", Name = "no blank line after comment when absent",
            Input    = "-- comment\nselect 2 from t",
            Expected = "-- comment\nselect\n\t2\nfrom t",
        },

        // ── inline comments inside conditions ─────────────────────────────────
        new TestCase {
            Rule = "inline-cmt", Name = "inline block comment between condition and and",
            Input    = "select a from t where name = 'a and b' /* and here */ and z = 1",
            Expected = "select\n\ta\nfrom t\nwhere\n\tname = 'a and b' /* and here */\n\tand z = 1",
        },
        new TestCase {
            Rule = "inline-cmt", Name = "inline comment on single where condition",
            Input    = "select a from t where x = 1 /* only */",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1 /* only */",
        },
        new TestCase {
            Rule = "inline-cmt", Name = "inline comment in join on",
            Input    = "select a from t1 s inner join t2 e on e.id = s.id /* join note */ and e.x = 1",
            Expected = "select\n\ta\nfrom t1 as s\n\tinner join t2 as e\n\t\ton e.id = s.id /* join note */\n\t\tand e.x = 1",
        },

        // ── GROUP BY / ORDER BY fragments ─────────────────────────────────────
        new TestCase {
            Rule = "fragment", Name = "bare group by list",
            Input    = "group by s.[BusinessEntityID], p.[Title], p.[FirstName]",
            Expected = "group by\n\ts.[BusinessEntityID],\n\tp.[Title],\n\tp.[FirstName]",
        },
        new TestCase {
            Rule = "fragment", Name = "bare group by single column",
            Input    = "group by s.[a]",
            Expected = "group by\n\ts.[a]",
        },
        new TestCase {
            Rule = "fragment", Name = "bare order by with directions",
            Input    = "order by a desc, b asc, c",
            Expected = "order by\n\ta desc,\n\tb asc,\n\tc",
        },

        // ── declare + comment edge cases (тест2/тест3) ────────────────────────
        new TestCase {
            Rule = "declare", Name = "next-line comment after declare stays standalone (bug 6)",
            Input    = "declare @a date = getdate()\n--next line comment\nselect 1 from t",
            Expected = "declare\n\t@a date = getdate()\n--next line comment\nselect\n\t1\nfrom t",
        },
        new TestCase {
            Rule = "declare", Name = "single variable on its own indented line",
            Input    = "declare @be int = 1",
            Expected = "declare\n\t@be int = 1",
        },
        new TestCase {
            Rule = "declare", Name = "comment glued to value (1--test2)",
            Input    = "declare @be int = 1--test2",
            Expected = "declare\n\t@be int = 1\t\t--test2",
        },
        new TestCase {
            Rule = "declare", Name = "standalone comment before declare",
            Input    = "--test\ndeclare @be int = 1",
            Expected = "--test\ndeclare\n\t@be int = 1",
        },
        new TestCase {
            Rule = "declare", Name = "exponent literal not broken by number lexing",
            Input    = "select 1e-5 as x",
            Expected = "select\n\t1e-5 as x",
        },
        new TestCase {
            Rule = "begincomment", Name = "comment attaches to declare inside begin/end",
            Input    = "begin\n\n--test\ndeclare @be int = 1--test2\n\ndeclare @b2e int = 1,\n@dt varchar(255) = ''\n\nend",
            Expected = "begin\n\n\t--test\n\tdeclare\n\t\t@be int = 1\t\t--test2\n\n\tdeclare\n\t\t@b2e int = 1,\n\t\t@dt varchar(255) = ''\n\nend",
        },
        new TestCase {
            Rule = "declarecomment", Name = "leading comment + declare with glued trailing comment (top level)",
            Input    = "--test\ndeclare @be int = 1--test2\n\ndeclare @b2e int = 1,\n@dt varchar(255) = ''",
            Expected = "--test\ndeclare\n\t@be int = 1\t\t--test2\n\ndeclare\n\t@b2e int = 1,\n\t@dt varchar(255) = ''",
        },
        new TestCase {
            Rule = "nstring", Name = "N-prefixed unicode string literals kept intact",
            Input    = "select @city=N'***',@utm=N'***',@report_sale_user=0,@process_status_id=-1,@source_title=N'***'",
            Expected = "select\n\t@city = N'***',\n\t@utm = N'***',\n\t@report_sale_user = 0,\n\t@process_status_id = -1,\n\t@source_title = N'***'",
        },
        new TestCase {
            Rule = "nstring", Name = "N identifier (not a string) untouched",
            Input    = "select N.id from Nodes N",
            Expected = "select\n\tN.id\nfrom Nodes as N",
        },
        new TestCase {
            Rule = "nstring", Name = "N-string with escaped quote",
            Input    = "select N'it''s ok' as x",
            Expected = "select\n\tN'it''s ok' as x",
        },

        // ── ANSI double-quoted identifiers ────────────────────────────────────
        new TestCase {
            Rule = "dquote", Name = "double-quoted identifiers kept intact",
            Input    = "select \"core_contract\".\"id\", \"auth_user\".\"username\" from \"core_contract\"",
            Expected = "select\n\t\"core_contract\".\"id\",\n\t\"auth_user\".\"username\"\nfrom \"core_contract\"",
        },
        new TestCase {
            Rule = "dquote", Name = "double-quoted with join and where",
            Input    = "select \"a\".\"id\" from \"t1\" a inner join \"t2\" b on (\"a\".\"id\" = \"b\".\"aid\") where \"a\".\"x\" = 1",
            Expected = "select\n\t\"a\".\"id\"\nfrom \"t1\" as a\n\tinner join \"t2\" as b\n\t\ton (\"a\".\"id\" = \"b\".\"aid\")\nwhere\n\t\"a\".\"x\" = 1",
        },
        new TestCase {
            Rule = "dquote", Name = "escaped double-quote inside identifier",
            Input    = "select \"a\"\"b\" from t",
            Expected = "select\n\t\"a\"\"b\"\nfrom t",
        },
        new TestCase {
            Rule = "dquote", Name = "single-quote string with double-quotes inside stays a string",
            Input    = "select 'text with \"quotes\" inside' as x from t",
            Expected = "select\n\t'text with \"quotes\" inside' as x\nfrom t",
        },

        // ── COLLATE and CASE with arithmetic in THEN/ELSE ─────────────────────
        new TestCase {
            Rule = "collate", Name = "collate clause on expression",
            Input    = "select name collate Latin1_General_Bin2 as x from t",
            Expected = "select\n\tname collate Latin1_General_Bin2 as x\nfrom t",
        },
        new TestCase {
            Rule = "case-arith", Name = "case else with arithmetic after parens",
            Input    = "select case when @e = -1 then 2147483647 else ((@a - @b)/2) + 1 end as x from t",
            Expected = "select\n\tcase\n\t\twhen @e = -1\n\t\tthen 2147483647\n\t\telse ((@a - @b) / 2) + 1\n\tend as x\nfrom t",
        },
        new TestCase {
            Rule = "case-arith", Name = "case then with arithmetic",
            Input    = "select case when @a = 1 then @x + @y * 2 else 0 end as z from t",
            Expected = "select\n\tcase\n\t\twhen @a = 1\n\t\tthen @x + @y * 2\n\t\telse 0\n\tend as z\nfrom t",
        },

        // ── function argument line-breaking (multiline args) ──────────────────
        new TestCase {
            Rule = "fnbreak", Name = "short function call stays inline",
            Input    = "select substring(est.text, 1, 5), nchar(31) from t",
            Expected = "select\n\tsubstring(est.text, 1, 5),\n\tnchar(31)\nfrom t",
        },
        new TestCase {
            Rule = "fnbreak", Name = "function with case arg breaks onto lines",
            Input    = "select replace(case when a=1 then 'x' else 'y' end, 'p', 'q') as z from t",
            Expected = "select\n\treplace(\n\t\tcase\n\t\t\twhen a = 1\n\t\t\tthen 'x'\n\t\t\telse 'y'\n\t\tend,\n\t\t'p',\n\t\t'q'\n\t) as z\nfrom t",
        },
        new TestCase {
            Rule = "fnbreak", Name = "function with subquery arg breaks onto lines",
            Input    = "select coalesce((select max(x) from u), 0) as m from t",
            Expected = "select\n\tcoalesce(\n\t\t(\n\t\t\tselect\n\t\t\t\tmax(x)\n\t\t\tfrom u\n\t\t),\n\t\t0\n\t) as m\nfrom t",
        },

        // ── DISTINCT / ALL inside aggregate functions ─────────────────────────
        new TestCase {
            Rule = "aggdistinct", Name = "count distinct simple column inline",
            Input    = "select count(distinct call_id) as c from t",
            Expected = "select\n\tcount(distinct call_id) as c\nfrom t",
        },
        new TestCase {
            Rule = "aggdistinct", Name = "count distinct case breaks onto lines",
            Input    = "select count(distinct case when x is not null then id end) as c from t",
            Expected = "select\n\tcount(\n\t\tdistinct case\n\t\t\twhen x is not null\n\t\t\tthen id\n\t\tend\n\t) as c\nfrom t",
        },

        // ── blank-line preservation between statements ────────────────────────
        new TestCase {
            Rule = "blanklines", Name = "no blank line added between statements when source has none",
            Input    = "select 1 from t\nselect 2 from t",
            Expected = "select\n\t1\nfrom t\nselect\n\t2\nfrom t",
        },
        new TestCase {
            Rule = "blanklines", Name = "blank line between statements preserved when present in source",
            Input    = "select 1 from t\n\nselect 2 from t",
            Expected = "select\n\t1\nfrom t\n\nselect\n\t2\nfrom t",
        },

        // ── CREATE TABLE: same-line paren, lowercased types ───────────────────
        new TestCase {
            Rule = "2.14", Name = "create table: opening paren on name line, types lowercased",
            Input    = "CREATE TABLE #x(\n\tprocess INT,\n\tprocess_status_id INT\n)",
            Expected = "create table #x (\n\tprocess int,\n\tprocess_status_id int\n)",
        },
        new TestCase {
            Rule = "2.14", Name = "if object_id drop/create kept compact and lowercased",
            Input    = "IF OBJECT_ID('TempDb..#x') is not null DROP TABLE #x\nCREATE TABLE #x(\n\tprocess INT,\n\tprocess_status_id INT\n)",
            Expected = "if object_id('TempDb..#x') is not null\ndrop table #x\ncreate table #x (\n\tprocess int,\n\tprocess_status_id int\n)",
        },
    };
}

}
