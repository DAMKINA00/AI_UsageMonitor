using System.Windows;

namespace AIUsageMonitor.UI;

/// <summary>Keep the order of touching characters until they separate.</summary>
public sealed class CharacterLayers(int count)
{
    readonly List<int> _backToFront = Enumerable.Range(0, count).ToList();
    readonly HashSet<(int A, int B)> _contacts = [];

    public IReadOnlyList<int> Update(IReadOnlyList<Rect> bodies, IReadOnlyList<bool> moving)
    {
        if (bodies.Count != _backToFront.Count || moving.Count != bodies.Count)
            throw new ArgumentException("Character bounds and movement must match the layer count.");
        var touching = new HashSet<(int A, int B)>();
        for (int a = 0; a < bodies.Count; a++)
            for (int b = a + 1; b < bodies.Count; b++)
            {
                var hit = Rect.Intersect(bodies[a], bodies[b]);
                if (!hit.IsEmpty && hit.Width > 0 && hit.Height > 0) touching.Add((a, b));
            }
        _contacts.IntersectWith(touching);
        foreach (var pair in touching)
        {
            if (_contacts.Contains(pair)) continue;
            if (moving[pair.A] != moving[pair.B])
            {
                int front = moving[pair.A] ? pair.A : pair.B;
                int back = moving[pair.A] ? pair.B : pair.A;
                var group = new HashSet<int> { front };
                bool changed;
                do
                {
                    changed = false;
                    foreach (var contact in _contacts)
                        if (group.Contains(contact.A) || group.Contains(contact.B))
                            changed |= group.Add(contact.A) | group.Add(contact.B);
                } while (changed);
                // Move an existing overlap group together so a third character cannot reverse its order.
                if (!group.Contains(back) && _backToFront.IndexOf(front) < _backToFront.IndexOf(back))
                {
                    var lifted = _backToFront.Where(group.Contains).ToArray();
                    _backToFront.RemoveAll(group.Contains);
                    _backToFront.InsertRange(_backToFront.IndexOf(back) + 1, lifted);
                }
            }
            _contacts.Add(pair);
        }
        return _backToFront;
    }
}
