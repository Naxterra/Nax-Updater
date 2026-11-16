using System.Collections.ObjectModel;

namespace NaxUpdater.Core.Services;

// Keep surviving item instances and containers when a live view gains, loses or
// reorders rows. Clear/Add resets selection and makes WinUI recreate every row.
public static class ObservableCollectionSync
{
    public static void Apply<T>(ObservableCollection<T> target, IEnumerable<T> items) where T : class
    {
        var desired = items.ToArray();
        var retained = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!retained.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < desired.Length; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], desired[i])) { existing = j; break; }
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, desired[i]);
        }
        while (target.Count > desired.Length) target.RemoveAt(target.Count - 1);
    }
}
