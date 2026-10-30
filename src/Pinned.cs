using System.Runtime.CompilerServices;

namespace LocalCoop
{
    /// <summary>
    /// A per-game-object table that survives .NET garbage collections.
    ///
    /// DCCM gives each game (HashLink) object a C# wrapper, but holds that wrapper only weakly unless the object is
    /// marked "stateful" (HashlinkObjManager, DCCM 35.14.1). After a .NET GC the same game object gets a NEW wrapper,
    /// and a ConditionalWeakTable keyed on the old wrapper silently loses its entry. That is what made marks such as
    /// "this item on the floor is player 2's copy" disappear after a few seconds (playtest 10: selling a copy left
    /// another copy, endlessly; player 2's outfit menu acting on player 1).
    /// Marking the object stateful keeps one wrapper for as long as the game object lives - the same thing DCCM's own
    /// ExtraDataUtils (obj.Meta) does.
    /// </summary>
    internal sealed class PinnedTable<TKey, TValue> where TKey : class where TValue : class?
    {
        private readonly ConditionalWeakTable<TKey, TValue> _t = new();

        public void AddOrUpdate(TKey key, TValue value)
        {
            Pin(key);
            _t.AddOrUpdate(key, value);
        }

        public bool TryGetValue(TKey key, out TValue value) => _t.TryGetValue(key, out value!);

        public bool Remove(TKey key) => _t.Remove(key);

        public TValue GetOrCreateValue(TKey key)
        {
            Pin(key);
            return _t.GetOrCreateValue(key);
        }

        internal static void Pin(object? o)
        {
            try { if (o is HaxeProxy.Runtime.HaxeProxyBase p) p.HashlinkObj.MarkStateful(); }
            catch { }
        }
    }
}
