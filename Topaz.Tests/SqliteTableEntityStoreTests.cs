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

    // --- Entity Group Transaction ($batch) behaviour -------------------------------------------------------------

    private static TableBatchAction Insert(string pk, string rk, string body) =>
        new(TableBatchOp.Insert, Scope, pk, rk, body, "*", false);

    private static TableBatchAction Merge(string pk, string rk, string body, string ifMatch = "*", bool upsert = false) =>
        new(TableBatchOp.Merge, Scope, pk, rk, body, ifMatch, upsert);

    private static TableBatchAction Delete(string pk, string rk, string ifMatch = "*") =>
        new(TableBatchOp.Delete, Scope, pk, rk, null, ifMatch, false);

    private static TableBatchAction Retrieve(string pk, string rk) =>
        new(TableBatchOp.Retrieve, Scope, pk, rk, null, "*", false);

    [Test]
    public void Batch_commits_all_operations_and_returns_per_op_results()
    {
        var results = _store.ExecuteBatch(new[] { Insert("p", "a", "{\"Name\":\"a\"}"), Insert("p", "b", "{\"Name\":\"b\"}") });

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].Etag, Is.Not.Null.And.Not.Empty);
        Assert.That(results[1].Etag, Is.Not.Null.And.Not.Empty);
        Assert.That(_store.Exists(Scope, "p", "a"), Is.True);
        Assert.That(_store.Exists(Scope, "p", "b"), Is.True);
    }

    [Test]
    public void Batch_rolls_back_every_operation_when_a_later_op_fails_its_precondition()
    {
        _store.Insert(Scope, "p", "a", "{\"V\":1}");
        Assert.That(JsonNode.Parse(_store.Get(Scope, "p", "a")!)!["V"]!.GetValue<int>(), Is.EqualTo(1),
            "precondition: V is 1 before the batch");

        // op0 would set V=2; op1 deletes a row that does not exist (EntityNotFound) -> the whole batch must roll back.
        var ex = Assert.Throws<TableBatchConflictException>(
            () => _store.ExecuteBatch(new[] { Merge("p", "a", "{\"V\":2}"), Delete("p", "missing") }));

        Assert.That(ex!.Index, Is.EqualTo(1), "the failing op is the delete at index 1");
        Assert.That(ex.InnerException, Is.InstanceOf<EntityNotFoundException>());
        Assert.That(JsonNode.Parse(_store.Get(Scope, "p", "a")!)!["V"]!.GetValue<int>(), Is.EqualTo(1),
            "op0 was rolled back: V is still 1, never 2");
    }

    [Test]
    public void Batch_insert_conflict_rolls_back_the_preceding_insert()
    {
        Assert.That(_store.Exists(Scope, "p", "x"), Is.False, "precondition: x absent");

        // op0 inserts x; op1 re-inserts x (sees op0's uncommitted write within the txn) -> EntityAlreadyExists.
        var ex = Assert.Throws<TableBatchConflictException>(
            () => _store.ExecuteBatch(new[] { Insert("p", "x", "{\"V\":1}"), Insert("p", "x", "{\"V\":2}") }));

        Assert.That(ex!.Index, Is.EqualTo(1));
        Assert.That(ex.InnerException, Is.InstanceOf<EntityAlreadyExistsException>());
        Assert.That(_store.Exists(Scope, "p", "x"), Is.False, "op0's insert was rolled back: x is absent");
    }

    [Test]
    public void Batch_is_read_your_write_within_the_transaction()
    {
        _store.ExecuteBatch(new[] { Insert("p", "a", "{\"V\":1}"), Merge("p", "a", "{\"W\":2}") });

        var stored = JsonNode.Parse(_store.Get(Scope, "p", "a")!)!;
        Assert.That(stored["V"]!.GetValue<int>(), Is.EqualTo(1), "the in-batch insert is visible to the in-batch merge");
        Assert.That(stored["W"]!.GetValue<int>(), Is.EqualTo(2));
    }

    [Test]
    public void Batch_merge_on_missing_inserts_only_when_upsert_is_allowed()
    {
        _store.ExecuteBatch(new[] { Merge("p", "new", "{\"V\":1}", upsert: true) });
        Assert.That(_store.Exists(Scope, "p", "new"), Is.True, "unconditional merge inserts a missing row");

        var ex = Assert.Throws<TableBatchConflictException>(
            () => _store.ExecuteBatch(new[] { Merge("p", "absent", "{\"V\":1}", ifMatch: "\"5\"", upsert: false) }));
        Assert.That(ex!.Index, Is.EqualTo(0));
        Assert.That(ex.InnerException, Is.InstanceOf<EntityNotFoundException>());
        Assert.That(_store.Exists(Scope, "p", "absent"), Is.False, "a conditional merge does not insert a missing row");
    }

    [Test]
    public void Batch_honours_a_per_op_if_match_and_rolls_back_on_a_stale_etag()
    {
        _store.Insert(Scope, "p", "a", "{\"V\":1}");

        // A stale If-Match must fail the op (and the batch) - the optimistic concurrency a losing writer hits.
        var ex = Assert.Throws<TableBatchConflictException>(
            () => _store.ExecuteBatch(new[] { Merge("p", "a", "{\"X\":1}", ifMatch: "\"0\"") }));

        Assert.That(ex!.Index, Is.EqualTo(0));
        Assert.That(ex.InnerException, Is.InstanceOf<UpdateConditionNotSatisfiedException>());
        Assert.That(JsonNode.Parse(_store.Get(Scope, "p", "a")!)!.AsObject().ContainsKey("X"), Is.False,
            "the conditional merge was rolled back");
    }

    [Test]
    public void Batch_retrieve_echoes_the_stored_entity()
    {
        _store.Insert(Scope, "p", "a", "{\"Name\":\"a\"}");

        var results = _store.ExecuteBatch(new[] { Retrieve("p", "a") });

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Body, Is.Not.Null);
        Assert.That(JsonNode.Parse(results[0].Body!)!["Name"]!.GetValue<string>(), Is.EqualTo("a"));
    }
}
