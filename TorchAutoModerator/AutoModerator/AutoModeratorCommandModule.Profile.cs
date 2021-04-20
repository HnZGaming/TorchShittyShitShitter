using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Profiler.Basics;
using Profiler.Core;
using Sandbox.Game.Entities;
using Torch.Commands;
using Torch.Commands.Permissions;
using Utils.General;
using Utils.Torch;
using VRage.Game.ModAPI;
using VRageMath;

namespace AutoModerator
{
    public sealed partial class AutoModeratorCommandModule
    {
        [Command("profile", "Self-profiler for players. `-this` or `-name=` to profile a specific grid.")]
        [Permission(MyPromoteLevel.None)]
        public void ProfilePlayer() => this.CatchAndReport(async () =>
        {
            Context.Player.ThrowIfNull("must be called by a player");

            // parse options
            var playerId = Context.Player.IdentityId;
            var gridId = (long?) null;
            var profileTime = 5.Seconds();
            var count = 4;
            foreach (var arg in Context.Args)
            {
                if (!CommandOption.TryGetOption(arg, out var option)) continue;

                if (option.IsParameterless("this"))
                {
                    var (found, grid) = await Context.Player.TryGetSelectedGrid();
                    if (!found)
                    {
                        Context.Respond("Grid not found", Color.Red);
                        return;
                    }

                    if (!grid.BigOwners.TryGetFirst(out var ownerId) || ownerId != playerId)
                    {
                        Context.Respond($"Not your grid: {grid.DisplayName}", Color.Red);
                        return;
                    }

                    gridId = grid.EntityId;
                    continue;
                }

                if (option.TryParse("name", out var name))
                {
                    if (!MyEntities.TryGetEntityByName(name, out var entity) ||
                        !(entity is MyCubeGrid grid))
                    {
                        Context.Respond($"Grid not found by name: {name}", Color.Red);
                        return;
                    }

                    gridId = grid.EntityId;
                    continue;
                }

                if (option.TryParseInt("time", out var time))
                {
                    profileTime = time.Seconds();
                    continue;
                }

                if (option.TryParseInt("count", out count))
                {
                    continue;
                }

                Context.Respond($"Unknown argument: {arg}", Color.Red);
                return;
            }

            Log.Debug($"player \"{Context.Player.DisplayName}\" self-profile; player: {playerId}, grid: {gridId}");

            Context.Respond($"Profiling for {profileTime.TotalSeconds} seconds...");

            var msgBuilder = new StringBuilder();
            msgBuilder.AppendLine();

            var mask = new GameEntityMask(playerId, gridId, null);
            using (var gridProfiler = new GridProfiler(mask))
            using (ProfilerResultQueue.Profile(gridProfiler))
            using (var blockProfiler = new BlockDefinitionProfiler(mask))
            using (ProfilerResultQueue.Profile(blockProfiler))
            {
                gridProfiler.MarkStart();
                blockProfiler.MarkStart();

                await Task.Delay(profileTime);

                msgBuilder.AppendLine("Grid lags (% of max lag per grid):");

                var gridProfileResult = gridProfiler.GetResult();
                foreach (var (grid, profilerEntry) in gridProfileResult.GetTopEntities(count))
                {
                    var gridName = grid.DisplayName;
                    var mspf = profilerEntry.MainThreadTime / gridProfileResult.TotalFrameCount;
                    var lagNormal = mspf / Config.MaxGridMspf;
                    msgBuilder.AppendLine($"\"{gridName}\" {lagNormal * 100:0}%");
                }

                msgBuilder.AppendLine();
                msgBuilder.AppendLine("Block lags (% of total per player or grid):");

                var blockLags = new Dictionary<string, double>();
                var blockProfilerResult = blockProfiler.GetResult();
                foreach (var (block, profileEntity) in blockProfilerResult.GetTopEntities(count))
                {
                    var blockName = block.BlockPairName;

                    blockLags.TryGetValue(blockName, out var lag);
                    lag += profileEntity.MainThreadTime;

                    blockLags[blockName] = lag;
                    Log.Trace($"player \"{Context.Player.DisplayName}\" self-profile block {blockName} {lag:0.00}ms");
                }

                var totalBlockLag = blockLags.Values.Sum();
                foreach (var (blockName, lag) in blockLags.OrderByDescending(p => p.Value))
                {
                    var relLag = lag / totalBlockLag;
                    msgBuilder.AppendLine($"{blockName} {relLag * 100:0}%");
                }

                Context.Respond(msgBuilder.ToString());
            }

            AutoModerator.OnSelfProfiled(playerId);
        });
    }
}