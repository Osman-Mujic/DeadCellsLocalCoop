using dc;
using dc.en;
using dc.en.inter;
using dc.tool;

namespace LocalCoop
{
    /// <summary>
    /// Team keys. The game asks "does THE hero have key X?" in many places, always on game.hero's (P1's)
    /// inventory: bank keycards (LockedDoorButton.hasKey f@25290, BankSubTeleporter.get_heroHasCard f@23419),
    /// key doors, graveyard grave keys (CemeteryGraveCave.hasKey/consumeKey f@23643/23647), the trigger zones that
    /// check for keys (HiddenTrigger.trigger f@6894), merchants and shrines that count keys, and more
    /// (bytecode scan: gamecode/invscan for "game.hero.inventory.hasItem/countItemKind/removeOne").
    /// Instead of fixing each place, keys are a shared key ring: when P1's or P2's inventory is asked about a key,
    /// the partner's keys count too, and using a key the asked player doesn't hold takes the partner's.
    /// Only key items (ids like *Key, *Key1, *Keycard, ALKey*); permanent runes (Meta*) and everything else untouched.
    /// </summary>
    public partial class LocalCoopMod
    {
        private void InitKeyRing()
        {
            Hook_Inventory.countItemKind += (orig, self, k) =>
            {
                int n = orig(self, k);
                var other = KeyPartnerInventory(self, k);
                return other != null ? n + orig(other, k) : n;
            };
            Hook_Inventory.hasItem += (orig, self, k) =>
            {
                if (orig(self, k)) return true;
                var other = KeyPartnerInventory(self, k);
                return other != null && orig(other, k);
            };
            Hook_Inventory.removeOne += (orig, self, k) =>
            {
                if (orig(self, k)) return true;
                var other = KeyPartnerInventory(self, k);
                if (other == null || !orig(other, k)) return false;
                Logger.Information("[LocalCoop] Key ring: used the partner's {Key}", k?.ToString());
                return true;
            };
            // Boss rooms lock behind whoever stepped on the trigger (it passes that hero to the entry cinematic).
            Hook_HiddenTrigger.trigger += (orig, self, by) =>
            {
                if (by is Hero h && (IsP2(h) || ReferenceEquals(h, P1))) _lastTriggerHero = h;
                orig(self, by);
            };
        }

        private Hero? _lastTriggerHero;

        /// <summary>The other player's inventory when <paramref name="inv"/> is a player's and <paramref name="k"/> is a key.</summary>
        private Inventory? KeyPartnerInventory(Inventory inv, dc.String? k)
        {
            if (k == null || P2 == null || P2.destroyed) return null;
            var p1 = P1;
            if (p1 == null) return null;
            Inventory? i1, i2;
            try { i1 = p1.inventory; i2 = P2.inventory; } catch { return null; }
            if (i1 == null || i2 == null) return null;
            var me = CoopInput.Ptr(inv);
            Inventory? other = me == CoopInput.Ptr(i1) ? i2 : me == CoopInput.Ptr(i2) ? i1 : null;
            if (other == null || !IsTeamKey(k)) return null;
            return other;
        }

        // Not cached by string pointer: a freed dynamic string's address can be reused by another string.
        private static bool IsTeamKey(dc.String k) => IsTeamKeyId(k.ToString());

        internal static bool IsTeamKeyId(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (id.StartsWith("Meta", StringComparison.Ordinal) || id.StartsWith("Richter", StringComparison.Ordinal)
                || id.Contains("Skin", StringComparison.Ordinal)) return false;
            if (id.Contains("Keycard", StringComparison.Ordinal) || id.StartsWith("ALKey", StringComparison.Ordinal)) return true;
            var t = id.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            return t.EndsWith("Key", StringComparison.Ordinal);
        }
    }
}
