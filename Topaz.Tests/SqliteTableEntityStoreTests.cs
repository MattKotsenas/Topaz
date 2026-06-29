using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Topaz.Service.Storage.Exceptions;
using Topaz.Service.Storage.Persistence;

namespace Topaz.Tests;

/// <summary>
/// Behaviour of the SQLite transactional table store: read-your-write, monotonic ETags, merge-vs-replace, and
/// conditional (If-Match) update/delete. These are the semantics a distributed job sequencer assumes of real Azure
/// Table Storage and that the file store could not guarantee under concurrent read-modify-write.
/// </summary>
[TestFixture]
public class SqliteTableEntityStoreTests
{
    private string _db = null!;
    private SqliteTableEntityStore _store = null!;
    private const string Scope = "acct/.table/Jobs";

    [SetUp]
    public void SetUp()
    {
        _db = Path.Combine(Path.GetTempPath(), "topaz-store-" + Guid.NewGuid().ToString("N"), "t.db");
        _store = SqliteTableEntityStore.ForDatabase(_db);
    }

    [TearDown]
    public void TearDown()
    {
        var dir = Path.GetDirectoryName(_db)!;
        if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public void Insert_then_get_is_read_your_write()
    {
        Assert.That(_store.Exists(Scope, "p", "r"), Is.False, "precondition: absent before insert");
        _store.Insert(Scope, "p", "r", "{\"Name\":\"a\"}");
        var body = _store.Get(Scope, "p", "r");
        Assert.That(body, Is.Not.Null);
        var node = JsonNode.Parse(body!)!;
        Assert.That(node["Name"]!.GetValue<string>(), Is.EqualTo("a"));
        Assert.That(node["PartitionKey"]!.GetValue<string>(), Is.EqualTo("p"));
        Assert.That(node["odata.etag"]!.GetValue<string>(), Is.Not.Empty);
    }

    [Test]
    public void Insert_duplicate_throws()
    {
        _store.Insert(Scope, "p", "r", "{}");
        Assert.Throws<EntityAlreadyExistsException>(() => _store.Insert(Scope, "p", "r", "{}"));
    }

    [Test]
    public void Etags_are_strictly_monotonic_across_writes()
    {
        long Etag(string b) => long.Parse(JsonNode.Parse(b)!["odata.etag"]!.GetValue<string>().Trim('"'));
        var e1 = Etag(_store.Upsert(Scope, "p", "r", "{}"));
        var e2 = Etag(_store.Upsert(Scope, "p", "r", "{}"));
        var e3 = Etag(_store.Upsert(Scope, "p", "r2", "{}"));
        Assert.That(e2, Is.GreaterThan(e1));
        Assert.That(e3, Is.GreaterThan(e2));
    }

    [Test]
    public void Merge_preserves_omitted_properties_replace_drops_them()
    {
        _store.Insert(Scope, "p", "r", "{\"A\":1,\"B\":2}");
        _store.Update(Scope, "p", "r", "{\"A\":9}", ifMatch: "*", merge: true);
        var merged = JsonNode.Parse(_store.Get(Scope, "p", "r")!)!;
        Assert.That(merged["A"]!.GetValue<int>(), Is.EqualTo(9));
        Assert.That(merged["B"]!.GetValue<int>(), Is.EqualTo(2), "merge keeps omitted B");

        _store.Update(Scope, "p", "r", "{\"A\":5}", ifMatch: "*", merge: false);
        var replaced = JsonNode.Parse(_store.Get(Scope, "p", "r")!)!;
        Assert.That(replaced.AsObject().ContainsKey("B"), Is.False, "replace drops omitted B");
    }

    [Test]
    public void Conditional_update_fails_on_stale_etag_succeeds_on_current()
    {
        var stored = _store.Insert(Scope, "p", "r", "{}");
        var current = JsonNode.Parse(stored)!["odata.etag"]!.GetValue<string>();
        Assert.Throws<UpdateConditionNotSatisfiedException>(
            () => _store.Update(Scope, "p", "r", "{\"X\":1}", ifMatch: "\"0\"", merge: true));
        Assert.DoesNotThrow(() => _store.Update(Scope, "p", "r", "{\"X\":1}", ifMatch: current, merge: true));
    }

    [Test]
    public void Delete_requires_existing_and_matching_etag()
    {
        Assert.Throws<EntityNotFoundException>(() => _store.Delete(Scope, "p", "r", "*"));
        _store.Insert(Scope, "p", "r", "{}");
        Assert.Throws<UpdateConditionNotSatisfiedException>(() => _store.Delete(Scope, "p", "r", "\"0\""));
        _store.Delete(Scope, "p", "r", "*");
        Assert.That(_store.Exists(Scope, "p", "r"), Is.False);
    }

    [Test]
    public void List_is_ordered_and_scope_isolated()
    {
        _store.Insert(Scope, "p", "b", "{}");
        _store.Insert(Scope, "p", "a", "{}");
        _store.Insert("other/.table/Jobs", "p", "z", "{}");
        var rows = _store.List(Scope).Select(b => JsonNode.Parse(b)!["RowKey"]!.GetValue<string>()).ToList();
        Assert.That(rows, Is.EqualTo(new[] { "a", "b" }));
    }
}
