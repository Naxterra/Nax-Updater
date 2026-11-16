using System.Collections.ObjectModel;
using System.Collections.Specialized;
using NaxUpdater.Core.Services;

internal static class CollectionRefreshRegression
{
    public static void Run(Action<bool, string> assert)
    {
        var first = new Row("first");
        var selected = new Row("selected");
        var last = new Row("last");
        var rows = new ObservableCollection<Row> { first, selected, last };
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => changes.Add(e.Action);
        ObservableCollectionSync.Apply(rows, [first, selected, last]);
        assert(changes.Count == 0 && ReferenceEquals(rows[1], selected), "An unchanged view reset its rows or selection anchor.");
        var added = new Row("added");
        ObservableCollectionSync.Apply(rows, [first, added, selected, last]);
        assert(changes.SequenceEqual([NotifyCollectionChangedAction.Add]) && ReferenceEquals(rows[2], selected),
            "One arriving result rebuilt unaffected rows instead of inserting one row.");
        changes.Clear();
        ObservableCollectionSync.Apply(rows, [last, selected, first]);
        assert(rows.SequenceEqual([last, selected, first]) && !changes.Contains(NotifyCollectionChangedAction.Reset) &&
            changes.Contains(NotifyCollectionChangedAction.Move), "Sorting/filtering replaced surviving row instances or reset the view.");
        changes.Clear();
        var snapshots = new List<Row>();
        for (var i = 0; i < 150; i++)
        {
            snapshots.Add(new Row("result " + i));
            ObservableCollectionSync.Apply(rows, snapshots);
        }
        assert(rows.SequenceEqual(snapshots) && changes.Count == 153 && !changes.Contains(NotifyCollectionChangedAction.Reset),
            "Streaming scan results repeatedly rebuilt the list instead of preserving published containers.");
        ObservableCollectionSync.Apply(rows, []);
        assert(rows.Count == 0 && !changes.Contains(NotifyCollectionChangedAction.Reset), "Clearing a filtered view emitted a full collection reset.");
    }

    private sealed record Row(string Id);
}
