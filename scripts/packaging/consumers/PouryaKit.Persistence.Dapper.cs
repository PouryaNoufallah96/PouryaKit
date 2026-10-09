using System.Data.Common;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.Dapper;

// The packed Dapper paging API, declared and mapped as a Feature's read would; no database is needed to build it.
var declaration = new PageDeclaration(["text"], [FilterField.Of<string>("text", FilterOperator.Contains)]);
const string From = "from notes.notes n where (@text_contains is null or strpos(n.text, @text_contains) > 0)";
var sql = new PageSql(declaration, select: $"select n.id, n.text {From}", count: $"select count(*) {From}")
    .OrderBy("text", "n.text")
    .ThenBy("n.id");
Func<DbConnection, CancellationToken, Task<Page<NoteListItem>>> read = (connection, cancellationToken) =>
    connection.QueryPageAsync<NoteListItem>(sql, new PageRequest(), cancellationToken: cancellationToken);
Console.WriteLine($"{sql.Declaration.Sorts.Count} {read.Method.ReturnType.Name}");

internal sealed record NoteListItem(Guid Id, string Text);
