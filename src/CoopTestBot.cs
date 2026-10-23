namespace LocalCoop
{
    /// <summary>In-game self test, filled in during QA. Enabled with LOCALCOOP_SELFTEST=1.</summary>
    internal static class CoopTestBot
    {
        public static bool Enabled = Environment.GetEnvironmentVariable("LOCALCOOP_SELFTEST") == "1";
        public static void Tick(LocalCoopMod mod, double dt) { }
    }
}
