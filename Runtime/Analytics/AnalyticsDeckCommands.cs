using Exerussus.AppCore.Deck;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>Команды консоли AppDeck: сбор вкл/выкл, состояние, сброс, профиль.</summary>
    internal static class AnalyticsDeckCommands
    {
        private static readonly object Owner = new();

        public static void Register()
        {
            AppDeck.UnregisterAll(Owner);

            AppDeck.Command("analytics.on")
                .Summary("Включить сбор аналитики")
                .Run(ctx =>
                {
                    AppAnalytics.SetCollecting(true);
                    ctx.Print("Аналитика: сбор включён");
                })
                .Register(Owner);

            AppDeck.Command("analytics.off")
                .Summary("Выключить сбор аналитики")
                .Run(ctx =>
                {
                    AppAnalytics.SetCollecting(false);
                    ctx.Print("Аналитика: сбор выключен");
                })
                .Register(Owner);

            AppDeck.Command("analytics.status")
                .Summary("Состояние аналитики: сбор, профиль, сессия, прогон, очередь")
                .Run(ctx =>
                {
                    ctx.Print($"сбор: {(AppAnalytics.CollectEnabled ? "вкл" : "выкл")}, пишется: {(AppAnalytics.IsCollecting ? "да" : "нет")}");
                    ctx.Print($"режим: {(AppAnalytics.IsLean ? "лёгкий" : "полный")}, адаптер: {AppAnalytics.AdapterName}");
                    ctx.Print($"профиль: {AppAnalytics.ProfileId}");
                    ctx.Print($"сессия: {AppAnalytics.SessionId}");
                    ctx.Print($"прогон: {AppAnalytics.RunId ?? "—"}");
                    ctx.Print($"в очереди пакетов: {AppAnalytics.QueuedPackets}");
                })
                .Register(Owner);

            AppDeck.Command("analytics.flush")
                .Summary("Закрыть текущий пакет и отдать на доставку")
                .Run(ctx =>
                {
                    AppAnalytics.Flush();
                    ctx.Print($"Пакет закрыт, в очереди: {AppAnalytics.QueuedPackets}");
                })
                .Register(Owner);

            AppDeck.Command("profile.new")
                .Summary("Сменить профиль игрока (новый player_<guid>)")
                .Run(ctx => ctx.Print($"Новый профиль: {AppAnalytics.NewProfile()}"))
                .Register(Owner);

            AppDeck.Command("profile.show")
                .Summary("Показать профиль игрока")
                .Run(ctx => ctx.Print(AppAnalytics.ProfileId))
                .Register(Owner);
        }

        public static void Unregister() => AppDeck.UnregisterAll(Owner);
    }
}
