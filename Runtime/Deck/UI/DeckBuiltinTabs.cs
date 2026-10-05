namespace Exerussus.AppCore.Deck
{
    /// <summary>Встроенные вкладки кроме Console (Actions, Metrics).</summary>
    internal static class DeckBuiltinTabs
    {
        public static void Register(DeckSettings settings)
        {
            AppDeck.AddTab(new ActionsTab(), AppDeck.BuiltinOwner);
            AppDeck.AddTab(new MetricsTab(settings), AppDeck.BuiltinOwner);
        }
    }
}
