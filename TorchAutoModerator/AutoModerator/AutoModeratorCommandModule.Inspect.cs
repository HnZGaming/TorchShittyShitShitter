using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutoModerator.Core;
using AutoModerator.Warnings;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Commands;
using Torch.Commands.Permissions;
using Utils.General;
using Utils.TimeSerieses;
using Utils.Torch;
using VRage.Game.ModAPI;
using VRageMath;

namespace AutoModerator
{
    public sealed partial class AutoModeratorCommandModule
    {
        [Command("inspect", "Show the time series of specified grid or player.")]
        [Permission(MyPromoteLevel.None)]
        public void Inspect() => this.CatchAndReport(async () =>
        {
            var calledByNormalPlayer = (Context.Player?.PromoteLevel ?? MyPromoteLevel.Admin) == MyPromoteLevel.None;
            var playerId = Context.Player?.IdentityId ?? 0L;
            var faction = MySession.Static.Factions.GetPlayerFaction(playerId);
            var memberIds = faction?.Members.Keys.ToSet() ?? new HashSet<long> {playerId};

            var top = 5;
            var showOutlierTests = false;
            var specificEntityIdOrNull = (long?) null;
            foreach (var arg in Context.Args)
            {
                if (!CommandOption.TryGetOption(arg, out var option)) continue;

                if (option.TryParseInt("top", out top)) continue;
                if (option.TryGetParameterlessBool("outlier", out showOutlierTests)) continue;

                if (option.IsParameterless("this"))
                {
                    var (found, grid) = await Context.Player.TryGetSelectedGrid();
                    if (!found)
                    {
                        Context.Respond("Grid not found", Color.Red);
                        return;
                    }

                    specificEntityIdOrNull = grid.EntityId;
                    continue;
                }

                if (option.TryParseLong("id", out var specificEntityId))
                {
                    if (!AutoModerator.TryGetTrackedEntity(specificEntityId, out _))
                    {
                        Context.Respond($"Entity not tracked: {specificEntityId}", Color.Red);
                        return;
                    }

                    specificEntityIdOrNull = specificEntityId;
                    continue;
                }

                if (option.TryParse("name", out var entityName))
                {
                    if (!AutoModerator.TryTraverseEntityByName(entityName, out var entity))
                    {
                        Context.Respond($"Entity not tracked by name: {entityName}", Color.Red);
                        return;
                    }

                    specificEntityIdOrNull = entity.Id;
                    continue;
                }

                if (option.IsParameterless("mine"))
                {
                    if (Context.Player?.PromoteLevel < MyPromoteLevel.Admin)
                    {
                        Context.Respond("Option allowed for admins only: mine", Color.Red);
                        return;
                    }

                    calledByNormalPlayer = true;
                    continue;
                }

                Context.Respond($"Invalid option: {arg}", Color.Red);
                return;
            }

            // inspect specific entity
            if (specificEntityIdOrNull is long entityId)
            {
                if (calledByNormalPlayer)
                {
                    if (!MyEntities.TryGetEntityById(entityId, out var entity))
                    {
                        Context.Respond("Entity not found", Color.Red);
                        return;
                    }

                    if (entity is IMyCharacter && !memberIds.Contains(entityId))
                    {
                        Context.Respond("Not you or your faction member", Color.Red);
                        return;
                    }

                    if (entity is MyCubeGrid grid)
                    {
                        if (!grid.BigOwners.TryGetFirst(out var ownerId))
                        {
                            Context.Respond("Not owned by anyone", Color.Red);
                            return;
                        }

                        if (!memberIds.Contains(ownerId))
                        {
                            Context.Respond("Not yours or your faction member's grid", Color.Red);
                            return;
                        }
                    }
                }

                InspectEntity(entityId, showOutlierTests);
                return;
            }

            Context.Respond("Gathering data...");

            var players = new List<TrackedEntitySnapshot>();
            foreach (var s in OrderForInspection(AutoModerator.GetTrackedPlayers()))
            {
                if (calledByNormalPlayer)
                {
                    if (!memberIds.Contains(s.Id)) continue;
                }

                players.Add(s);
            }

            var grids = new List<TrackedEntitySnapshot>();
            foreach (var s in OrderForInspection(AutoModerator.GetTrackedGrids()).Take(top))
            {
                if (calledByNormalPlayer)
                {
                    if (!memberIds.Contains(s.OwnerId)) continue;
                }

                grids.Add(s);
            }

            var msgBuilder = new StringBuilder();
            msgBuilder.AppendLine();

            if (players.Any())
            {
                msgBuilder.AppendLine("Players:");

                var warningStates = AutoModerator.GetWarningState();
                foreach (var s in players)
                {
                    var warning = warningStates.GetOrElse(s.Id, null);
                    var line = MakeTrackedEntityLine(s, warning);
                    msgBuilder.AppendLine(line);
                }
            }
            else
            {
                msgBuilder.AppendLine("No players");
            }

            if (grids.Any())
            {
                msgBuilder.AppendLine("Grids:");

                foreach (var s in grids)
                {
                    var line = MakeTrackedEntityLine(s);
                    msgBuilder.AppendLine(line);
                }
            }
            else
            {
                msgBuilder.AppendLine("No grids");
            }

            Context.Respond(msgBuilder.ToString());
        });

        string MakeTrackedEntityLine(TrackedEntitySnapshot s, LagPlayerState w = null)
        {
            var lagGraph = MakeOnelinerGraph(30, 1, s.LongLagNormal);

            var pinSecs = s.RemainingTime.TotalSeconds;
            var pinSecsNormal = pinSecs / Config.PunishTime;
            var pinGraph = MakeOnelinerGraph(30, 1, pinSecsNormal, false);
            pinGraph = s.IsPinned ? $"{pinGraph} pin {pinSecs:0} secs" : "no pin";
            var warning = w?.Quest > LagQuest.None ? $"warning: {w.Quest} ({w.LastWarningLagNormal * 100:0}%)" : "";

            return $"{lagGraph} {pinGraph} {s.Name} ({s.Id}) {warning}";
        }

        static IEnumerable<TrackedEntitySnapshot> OrderForInspection(IEnumerable<TrackedEntitySnapshot> snapshots)
        {
            var pinnedResults = new List<TrackedEntitySnapshot>();
            var otherResults = new List<TrackedEntitySnapshot>();
            foreach (var snapshot in snapshots)
            {
                if (snapshot.IsPinned)
                {
                    pinnedResults.Add(snapshot);
                }
                else
                {
                    otherResults.Add(snapshot);
                }
            }

            pinnedResults.Sort(TrackedEntitySnapshot.LongLagComparer.Instance);
            otherResults.Sort(TrackedEntitySnapshot.LongLagComparer.Instance);
            return pinnedResults.Concat(otherResults);
        }

        void InspectEntity(long entityId, bool showOutlierTests)
        {
            if (!AutoModerator.TryGetTimeSeries(entityId, out var timeSeries))
            {
                Context.Respond("Time series not found", Color.Red);
                return;
            }

            if (!AutoModerator.TryGetTrackedEntity(entityId, out var entity))
            {
                Context.Respond("Tracked entity not found", Color.Red);
                return;
            }

            if (timeSeries.Count == 0)
            {
                Context.Respond("Time series found but empty", Color.Red);
                return;
            }

            Context.Respond("Gathering data...");

            var outlierTests = timeSeries.TestOutlier();
            var series = timeSeries.Zip(outlierTests);

            var msgBuilder = new StringBuilder();
            msgBuilder.AppendLine();

            msgBuilder.AppendLine($"{entity.Name} ({entity.Id})");
            msgBuilder.AppendLine($"Owner: {entity.OwnerName} ({entity.OwnerId})");

            if (entity.IsBlessed)
            {
                msgBuilder.AppendLine("Blessed (just spawned)");
            }

            msgBuilder.AppendLine($"Lag (evaluated): {entity.LongLagNormal * 100:0}%");
            msgBuilder.AppendLine(entity.IsPinned ? $"Pinned for next {entity.RemainingTime.TotalSeconds} seconds" : "Not pinned");

            if (AutoModerator.GetWarningState().TryGetValue(entityId, out var w))
            {
                msgBuilder.AppendLine($"Warning: {w.Quest} (Normal: {w.LastWarningLagNormal * 100:0}%)");
            }

            foreach (var (((_, normal), outlierTest), index) in series.Indexed())
            {
                const int MaxWidth = 30;

                var normalGraph = MakeOnelinerGraph(MaxWidth, 1, normal);
                var outlierGraph = showOutlierTests ? MakeOnelinerGraph(MaxWidth, 3, outlierTest) : "";
                msgBuilder.AppendLine($"{index:000} {normalGraph} {outlierGraph}");
            }

            Context.Respond(msgBuilder.ToString());
        }

        static string MakeOnelinerGraph(int maxWidth, double maxNormal, double normal, bool showLabel = true)
        {
            normal = normal.IsValid() ? normal : 0;
            var graphNormal = Math.Min(1, Math.Max(0, normal / maxNormal));
            var size = Math.Min(maxWidth, (int) (graphNormal * maxWidth));
            var graph0 = Enumerable.Repeat('¦', size);
            var graph1 = Enumerable.Repeat('\'', maxWidth - size);
            var graph = new string(graph0.Concat(graph1).ToArray());
            var label = showLabel ? $" {normal * 100:000}%" : "";
            return $"{graph}{label}";
        }
    }
}