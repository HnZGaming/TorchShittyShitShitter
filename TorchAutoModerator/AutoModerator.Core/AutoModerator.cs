using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoModerator.Punishes;
using AutoModerator.Punishes.Broadcasts;
using AutoModerator.Warnings;
using NLog;
using Profiler.Basics;
using Profiler.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Torch.API.Managers;
using Utils.General;
using Utils.TimeSerieses;
using Utils.Torch;

namespace AutoModerator.Core
{
    public sealed class AutoModerator
    {
        static readonly ILogger Log = LogManager.GetCurrentClassLogger();

        public interface IConfig :
            GridLagTracker.IConfig,
            PlayerLagTracker.IConfig,
            BroadcastListenerCollection.IConfig,
            EntityGpsBroadcaster.IConfig,
            LagWarningTracker.IConfig,
            LagPunishExecutor.IConfig,
            LagPunishChatFeed.IConfig,
            LagQuestlogCollection.IConfig,
            LagNotificationCollection.IConfig,
            LagWarningChatFeed.IConfig
        {
            bool IsEnabled { get; }
            double FirstIdleTime { get; }
            double IntervalFrequency { get; }
            bool EnablePunishChatFeed { get; }
            IEnumerable<string> ExemptBlockTypePairs { get; }
            void RemoveExemptBlockType(string input);
        }

        readonly IConfig _config;
        readonly GridLagTracker _laggyGrids;
        readonly PlayerLagTracker _laggyPlayers;
        readonly EntityGpsBroadcaster _entityGpsBroadcaster;
        readonly BroadcastListenerCollection _gpsReceivers;
        readonly LagWarningTracker _lagWarningTracker;
        readonly LagPunishExecutor _punishExecutor;
        readonly LagPunishChatFeed _punishChatFeed;
        readonly IChatManagerServer _chatManager;
        readonly BlockTypePairCollection _exemptBlockTypePairs;

        public AutoModerator(IConfig config, IChatManagerServer chatManager)
        {
            _config = config;
            _chatManager = chatManager;
            _exemptBlockTypePairs = new BlockTypePairCollection();
            _laggyGrids = new GridLagTracker(config);
            _laggyPlayers = new PlayerLagTracker(config);
            _gpsReceivers = new BroadcastListenerCollection(config);
            _entityGpsBroadcaster = new EntityGpsBroadcaster(config);
            _lagWarningTracker = new LagWarningTracker(config);
            _punishExecutor = new LagPunishExecutor(config, _exemptBlockTypePairs);
            _punishChatFeed = new LagPunishChatFeed(config, _chatManager);

            _lagWarningTracker.AddListener(new LagQuestlogCollection(config));
            _lagWarningTracker.AddListener(new LagNotificationCollection(config));
            _lagWarningTracker.AddListener(new LagWarningChatFeed(config, _chatManager));
        }

        public void Close()
        {
            _entityGpsBroadcaster.ClearGpss();
            _lagWarningTracker.Clear();
            _lagWarningTracker.ClearListeners();
        }

        public async Task Main(CancellationToken canceller)
        {
            Log.Info("started main");

            _entityGpsBroadcaster.ClearGpss();
            _lagWarningTracker.Clear();

            // Wait for some time during the session startup
            await TaskUtils.Delay(() => _config.FirstIdleTime.Seconds(), 1.Seconds(), canceller);

            Log.Info("started collector loop");

            // MAIN LOOP
            while (!canceller.IsCancellationRequested)
            {
                if (!_config.IsEnabled)
                {
                    _laggyGrids.Clear();
                    _laggyPlayers.Clear();
                    _entityGpsBroadcaster.ClearGpss();
                    _lagWarningTracker.Clear();
                    _punishExecutor.Clear();
                    _punishChatFeed.Clear();

                    await Task.Delay(1.Seconds(), canceller);
                    return;
                }

                await Profile(canceller);
                Warn();
                AnnouncePunishments();
                FixExemptBlockTypeCollection();
                await Punish(canceller);
                await AnnounceDeletedGrids(canceller);

                Log.Debug("interval done");
            }
        }

        async Task Profile(CancellationToken canceller)
        {
            // auto profile
            var mask = new GameEntityMask(null, null, null);
            using (var gridProfiler = new GridProfiler(mask))
            using (var playerProfiler = new PlayerProfiler(mask))
            using (ProfilerResultQueue.Profile(gridProfiler))
            using (ProfilerResultQueue.Profile(playerProfiler))
            {
                Log.Trace("auto-profile started");
                gridProfiler.MarkStart();
                playerProfiler.MarkStart();
                await Task.Delay(_config.IntervalFrequency.Seconds(), canceller);
                Log.Trace("auto-profile done");

                _laggyGrids.Update(gridProfiler.GetResult());
                _laggyPlayers.Update(playerProfiler.GetResult());
            }

            Log.Trace("profile done");
        }

        void Warn()
        {
            var usePins = _config.PunishType != LagPunishType.None;
            Log.Debug($"punishment type: {_config.PunishType}, warning for punishment: {usePins}");

            var sources = new List<LagWarningSource>();
            var players = _laggyPlayers.GetTrackedEntities(_config.WarningLagNormal).ToDictionary(p => p.Id);
            var grids = _laggyGrids.GetPlayerLaggiestGrids(_config.WarningLagNormal).ToDictionary();
            foreach (var (playerId, (player, grid)) in players.Zip(grids))
            {
                if (playerId == 0) continue; // grid not owned

                var src = new LagWarningSource(
                    playerId,
                    MySession.Static.Players.GetPlayerNameOrElse(playerId, $"<{playerId}>"),
                    player.LongLagNormal,
                    usePins ? player.RemainingTime : TimeSpan.Zero,
                    grid.LongLagNormal,
                    usePins ? grid.RemainingTime : TimeSpan.Zero);

                sources.Add(src);
            }

            _lagWarningTracker.Update(sources);

            Log.Trace("warnings done");
        }

        void AnnouncePunishments()
        {
            if (!_config.EnablePunishChatFeed)
            {
                _punishChatFeed.Clear();
                return;
            }

            var sources = new List<LagPunishChatSource>();
            var grids = _laggyGrids.GetPlayerPinnedGrids().ToDictionary();
            var players = _laggyPlayers.GetPinnedPlayers().ToDictionary(p => p.Id);
            foreach (var (playerId, (laggiestGrid, player)) in grids.Zip(players))
            {
                var lagNormal = Math.Max(laggiestGrid.LongLagNormal, player.LongLagNormal);
                var isPinned = laggiestGrid.IsPinned || player.IsPinned;
                var source = new LagPunishChatSource(playerId, player.Name, player.FactionTag, laggiestGrid.Id, laggiestGrid.Name, lagNormal, isPinned);
                sources.Add(source);
            }

            _punishChatFeed.Update(sources);
        }

        void FixExemptBlockTypeCollection()
        {
            var invalidInputs = new List<string>();

            _exemptBlockTypePairs.Clear();
            foreach (var rawInput in _config.ExemptBlockTypePairs)
            {
                if (!_exemptBlockTypePairs.TryAdd(rawInput))
                {
                    invalidInputs.Add(rawInput);
                    Log.Warn($"Removed invalid block type pair: {rawInput}");
                }
            }

            // remove invalid items from the config
            foreach (var invalidInput in invalidInputs)
            {
                _config.RemoveExemptBlockType(invalidInput);
            }
        }

        async Task Punish(CancellationToken canceller)
        {
            await PunishBlocks();
            await BroadcastLaggyGrids(canceller);
        }

        async Task PunishBlocks()
        {
            if (_config.PunishType != LagPunishType.Damage &&
                _config.PunishType != LagPunishType.Shutdown)
            {
                _punishExecutor.Clear();
                return;
            }

            var punishSources = new Dictionary<long, LagPunishSource>();
            foreach (var pinnedPlayer in _laggyPlayers.GetPinnedPlayers())
            {
                var playerId = pinnedPlayer.Id;
                if (!_laggyGrids.TryGetLaggiestGridOwnedBy(playerId, out var laggiestGrid)) continue;

                var src = new LagPunishSource(laggiestGrid.Id, laggiestGrid.IsPinned);
                punishSources[src.GridId] = src;
            }

            foreach (var grid in _laggyGrids.GetPinnedGrids())
            {
                var gpsSource = new LagPunishSource(grid.Id, grid.IsPinned);
                punishSources[gpsSource.GridId] = gpsSource;
            }

            await _punishExecutor.Update(punishSources);

            Log.Trace("punishment done");
        }

        async Task BroadcastLaggyGrids(CancellationToken canceller)
        {
            if (_config.PunishType != LagPunishType.Broadcast)
            {
                _entityGpsBroadcaster.ClearGpss();
                return;
            }

            var allGpsSources = new Dictionary<long, GridGpsSource>();

            foreach (var (player, rank) in _laggyPlayers.GetPinnedPlayers().Indexed())
            {
                var playerId = player.Id;
                if (!_laggyGrids.TryGetLaggiestGridOwnedBy(playerId, out var laggiestGrid)) continue;

                var gpsSource = new GridGpsSource(laggiestGrid.Id, player.LongLagNormal, player.RemainingTime, rank);
                allGpsSources[gpsSource.GridId] = gpsSource;
            }

            foreach (var (grid, rank) in _laggyGrids.GetPinnedGrids().Indexed())
            {
                var gpsSource = new GridGpsSource(grid.Id, grid.LongLagNormal, grid.RemainingTime, rank);
                allGpsSources[gpsSource.GridId] = gpsSource;
            }

            var targetIdentityIds = _gpsReceivers.GetReceiverIdentityIds();
            await _entityGpsBroadcaster.ReplaceGpss(allGpsSources.Values, targetIdentityIds, canceller);

            Log.Trace("broadcast done");
        }

        async Task AnnounceDeletedGrids(CancellationToken canceller)
        {
            // stop tracking deleted grids & report cheating
            // we're doing this right here to get the max chance of grabbing the owner name
            var lostGrids = new List<TrackedEntitySnapshot>();
            var trackedGrids = _laggyGrids.GetTrackedEntities();

            await GameLoopObserver.MoveToGameLoop(canceller);

            foreach (var trackedGrid in trackedGrids)
            {
                if (!MyEntities.TryGetEntityById(trackedGrid.Id, out _))
                {
                    lostGrids.Add(trackedGrid);
                }
            }

            await TaskUtils.MoveToThreadPool(canceller);

            foreach (var lostGrid in lostGrids)
            {
                _laggyGrids.StopTracking(lostGrid.Id);

                if (lostGrid.LongLagNormal < _config.WarningLagNormal) continue;

                var gridName = lostGrid.Name;
                var ownerName = lostGrid.OwnerName;
                Log.Warn($"Laggy grid deleted by player: {gridName}: {ownerName}");

                if (_config.EnablePunishChatFeed)
                {
                    _chatManager.SendMessage(_config.PunishReportChatName, 0, $"Laggy grid deleted by player: {gridName}: {ownerName}");
                }
            }

            Log.Trace("announcing deleted entities done");
        }

        public IEnumerable<MyGps> GetAllGpss()
        {
            return _entityGpsBroadcaster.GetGpss();
        }

        public void OnSelfProfiled(long playerId)
        {
            _lagWarningTracker.OnSelfProfiled(playerId);
        }

        public void ClearCache()
        {
            _laggyGrids.Clear();
            _laggyPlayers.Clear();
        }

        public void ClearQuestForUser(long playerId)
        {
            _lagWarningTracker.Remove(playerId);
        }

        public bool TryGetTimeSeries(long entityId, out ITimeSeries<double> timeSeries)
        {
            return _laggyGrids.TryGetTimeSeries(entityId, out timeSeries) ||
                   _laggyPlayers.TryGetTimeSeries(entityId, out timeSeries);
        }

        public bool TryGetTrackedEntity(long entityId, out TrackedEntitySnapshot entity)
        {
            return _laggyGrids.TryGetTrackedEntity(entityId, out entity) ||
                   _laggyPlayers.TryGetTrackedEntity(entityId, out entity);
        }

        public bool TryTraverseEntityByName(string name, out TrackedEntitySnapshot entity)
        {
            return _laggyGrids.TryTraverseTrackedEntityByName(name, out entity) ||
                   _laggyPlayers.TryTraverseTrackedEntityByName(name, out entity);
        }

        public bool TryTraverseTrackedPlayerById(long playerId, out string playerName)
        {
            if (_laggyGrids.TryGetLaggiestGridOwnedBy(playerId, out var grid))
            {
                playerName = grid.OwnerName;
                return true;
            }

            if (_laggyPlayers.TryGetTrackedEntity(playerId, out var p))
            {
                playerName = p.Name;
                return true;
            }

            playerName = default;
            return false;
        }

        public bool TryTraverseTrackedPlayerByName(string playerName, out long playerId)
        {
            if (_laggyGrids.TryTraverseGridOwnerByName(playerName, out var p))
            {
                playerId = p.PlayerId;
                return true;
            }

            if (_laggyPlayers.TryTraverseTrackedEntityByName(playerName, out var pp))
            {
                playerId = pp.Id;
                return true;
            }

            playerId = default;
            return false;
        }

        public IReadOnlyDictionary<long, LagPlayerState> GetWarningState()
        {
            return _lagWarningTracker.GetInternalSnapshot();
        }

        public IEnumerable<TrackedEntitySnapshot> GetTrackedGrids()
        {
            return _laggyGrids.GetTrackedEntities();
        }

        public IEnumerable<TrackedEntitySnapshot> GetTrackedPlayers()
        {
            return _laggyPlayers.GetTrackedEntities();
        }

        public bool TryGetLaggiestGridOwnedBy(long playerId, out TrackedEntitySnapshot grid)
        {
            return _laggyGrids.TryGetLaggiestGridOwnedBy(playerId, out grid);
        }
    }
}