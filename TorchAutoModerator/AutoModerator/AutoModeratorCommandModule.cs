using System.Linq;
using System.Text;
using NLog;
using Torch.Commands;
using Torch.Commands.Permissions;
using Utils.General;
using Utils.Torch;
using VRage.Game.ModAPI;
using VRageMath;

namespace AutoModerator
{
    [Category("lag")]
    public sealed partial class AutoModeratorCommandModule : CommandModule
    {
        static readonly ILogger Log = LogManager.GetCurrentClassLogger();

        AutoModeratorPlugin Plugin => (AutoModeratorPlugin) Context.Plugin;
        AutoModeratorConfig Config => Plugin.Config;
        Core.AutoModerator AutoModerator => Plugin.AutoModerator;

        [Command("configs", "Get or set config")]
        [Permission(MyPromoteLevel.Admin)]
        public void GetOrSetConfig() => this.CatchAndReport(() =>
        {
            this.GetOrSetProperty(Plugin.Config);
        });

        [Command("commands", "Get a list of commands")]
        [Permission(MyPromoteLevel.Admin)]
        public void ShowCommandList() => this.CatchAndReport(() =>
        {
            this.ShowCommands();
        });

        [Command("clear", "Clear all internal state.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Clear() => this.CatchAndReport(() =>
        {
            AutoModerator.ClearCache();
            Context.Respond("cleared all internal state");
        });

        [Command("gpslist", "Show the list of GPS entities.")]
        [Permission(MyPromoteLevel.Admin)]
        public void ShowGpss() => this.CatchAndReport(() =>
        {
            var gpss = AutoModerator.GetAllGpss();

            if (!gpss.Any())
            {
                Context.Respond("No GPS entities found");
                return;
            }

            var msgBuilder = new StringBuilder();
            foreach (var gps in gpss)
            {
                msgBuilder.AppendLine($"> {gps.Name}");
            }

            Context.Respond($"Custom GPS entities: \n{msgBuilder}");
        });

        [Command("gpsmute", "Mute broadcasting.")]
        [Permission(MyPromoteLevel.None)]
        public void MuteBroadcastsToPlayer() => this.CatchAndReport(() =>
        {
            if (Context.Player == null)
            {
                Context.Respond("Can be called by a player only", Color.Red);
                return;
            }

            Config.AddMutedPlayer(Context.Player.SteamUserId);
            Context.Respond("Muted broadcasting. It may take some time to take effect.");
        });

        [Command("gpsunmute", "Unmute broadcasting.")]
        [Permission(MyPromoteLevel.None)]
        public void UnmuteBroadcastsToPlayer() => this.CatchAndReport(() =>
        {
            if (Context.Player == null)
            {
                Context.Respond("Can be called by a player only", Color.Red);
                return;
            }

            Config.RemoveMutedPlayer(Context.Player.SteamUserId);
            Context.Respond("Unmuted broadcasting. It may take some time to take effect.");
        });

        [Command("gpsunmuteall", "Force every player to unmute broadcasting.")]
        [Permission(MyPromoteLevel.Admin)]
        public void UnmuteBroadcastsToAll() => this.CatchAndReport(() =>
        {
            Config.RemoveAllMutedPlayers();
        });

        [Command("clearwarning", "Clear quest HUD.")]
        [Permission(MyPromoteLevel.None)]
        public void ClearQuests() => this.CatchAndReport(() =>
        {
            Context.Player.ThrowIfNull("must be called by a player");
            AutoModerator.ClearQuestForUser(Context.Player.IdentityId);
        });

        [Command("laggiest", "Get the laggiest grid of player. -id or -name to specify the player name other than yourself.")]
        [Permission(MyPromoteLevel.None)]
        public void ShowLaggiest() => this.CatchAndReport(() =>
        {
            var playerId = 0L;
            var playerName = "";
            foreach (var arg in Context.Args)
            {
                if (!CommandOption.TryGetOption(arg, out var option)) continue;

                if (option.TryParseLong("id", out playerId))
                {
                    if (!AutoModerator.TryTraverseTrackedPlayerById(playerId, out playerName))
                    {
                        Context.Respond($"Online player not found: {playerId}", Color.Red);
                        return;
                    }

                    continue;
                }

                if (option.TryParse("name", out playerName))
                {
                    if (!AutoModerator.TryTraverseTrackedPlayerByName(playerName, out playerId))
                    {
                        Context.Respond($"Online player not found: {playerName}", Color.Red);
                        return;
                    }

                    continue;
                }

                Context.Respond($"Unknown option: {arg}", Color.Red);
                return;
            }

            if (playerId == 0)
            {
                Context.Respond("No input", Color.Red);
                return;
            }

            if (Context.Player is IMyPlayer caller &&
                caller.PromoteLevel < MyPromoteLevel.Moderator &&
                !caller.IsFriendWith(playerId))
            {
                Context.Respond($"You're not a friend of this player: {playerName}", Color.Red);
                return;
            }

            if (!AutoModerator.TryGetLaggiestGridOwnedBy(playerId, out var grid))
            {
                Context.Respond($"No grid tracked for player: {playerName ?? $"<{playerId}>"}");
                return;
            }

            Context.Respond($"Laggiest grid owned by player \"{playerName}\": \"{grid.Name}\" ({grid.LongLagNormal * 100:0}%)");
        });
    }
}