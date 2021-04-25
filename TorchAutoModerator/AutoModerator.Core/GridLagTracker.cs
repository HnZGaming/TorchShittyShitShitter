using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NLog;
using Profiler.Basics;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Utils.General;
using Utils.TimeSerieses;
using Utils.Torch;
using VRage.Game.ModAPI;

namespace AutoModerator.Core
{
    public sealed class GridLagTracker
    {
        public interface IConfig
        {
            double MaxGridMspf { get; }
            double TrackingTime { get; }
            double PunishTime { get; }
            double GracePeriodTime { get; }
            double OutlierFenceNormal { get; }
            bool IsIdentityExempt(long identityId);
        }

        sealed class BridgeConfig : EntityLagTracker.IConfig
        {
            readonly IConfig _masterConfig;

            public BridgeConfig(IConfig masterConfig)
            {
                _masterConfig = masterConfig;
            }

            public double PinLag => _masterConfig.MaxGridMspf;
            public double OutlierFenceNormal => _masterConfig.OutlierFenceNormal;
            public TimeSpan TrackingSpan => _masterConfig.TrackingTime.Seconds();
            public TimeSpan PinSpan => _masterConfig.PunishTime.Seconds();
            public TimeSpan GracePeriodSpan => _masterConfig.GracePeriodTime.Seconds();
            public bool IsIdentityExempt(long id) => _masterConfig.IsIdentityExempt(id);
        }

        static readonly ILogger Log = LogManager.GetCurrentClassLogger();
        readonly EntityLagTracker _lagTracker;
        readonly Dictionary<long, long> _ownerToLaggiestGrids;

        public GridLagTracker(IConfig config)
        {
            _lagTracker = new EntityLagTracker(new BridgeConfig(config));
            _ownerToLaggiestGrids = new Dictionary<long, long>();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGetLaggiestGridOwnedBy(long ownerId, out TrackedEntitySnapshot grid)
        {
            grid = default;
            return _ownerToLaggiestGrids.TryGetValue(ownerId, out var gridId) &&
                   _lagTracker.TryGetTrackedEntity(gridId, out grid);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryTraverseGridOwnerByName(string ownerName, out (long PlayerId, string PlayerName) grid)
        {
            if (_lagTracker.GetTrackedEntities().TryGetFirst(e => e.OwnerName == ownerName, out var g))
            {
                grid = (g.OwnerId, g.OwnerName);
                return true;
            }

            grid = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public IEnumerable<(long OwnerId, TrackedEntitySnapshot Grid)> GetPlayerLaggiestGrids(double maxLongLagNormal)
        {
            foreach (var (ownerId, laggiestGridId) in _ownerToLaggiestGrids)
            {
                if (!_lagTracker.TryGetTrackedEntity(laggiestGridId, out var grid))
                {
                    Log.Warn($"grid deleted by player: {grid.Id} by {ownerId}");
                    continue;
                }

                if (grid.LongLagNormal <= maxLongLagNormal) continue;

                yield return (ownerId, grid);
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public IEnumerable<(long OwnerId, TrackedEntitySnapshot Grid)> GetPlayerPinnedGrids()
        {
            foreach (var (ownerId, laggiestGridId) in _ownerToLaggiestGrids)
            {
                if (!_lagTracker.TryGetTrackedEntity(laggiestGridId, out var grid))
                {
                    Log.Warn($"grid deleted by player: {grid.Id} by {ownerId}");
                    continue;
                }

                if (!grid.IsPinned) continue;

                yield return (ownerId, grid);
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Update(BaseProfilerResult<MyCubeGrid> profileResult)
        {
            Log.Trace("updating grid lags...");

            var lags = new List<EntityLagSource>();
            var latestLaggiestGridToOwnerIds = new Dictionary<long, long>();
            var ownerIds = new HashSet<long>();

            foreach (var (grid, profileEntity) in profileResult.GetTopEntities(20))
            {
                var mspf = profileEntity.MainThreadTime / profileResult.TotalFrameCount;
                var faction = grid.BigOwners.Concat(grid.SmallOwners).TryGetFirst(out var ownerId)
                    ? MySession.Static.Factions.GetPlayerFaction(ownerId) as IMyFaction
                    : null;
                var factionId = faction?.FactionId ?? 0L;
                var factionTag = faction?.Tag ?? "<single>";

                var ownerName = MySession.Static.Players.TryGetPlayerById(ownerId, out var p) ? p.DisplayName : $"<{ownerId}>";
                var lag = new EntityLagSource(grid.EntityId, grid.DisplayName, ownerId, ownerName, factionId, factionTag, mspf);
                lags.Add(lag);

                if (!ownerIds.Contains(ownerId)) // pick the laggiest grid
                {
                    ownerIds.Add(ownerId);
                    latestLaggiestGridToOwnerIds.Add(grid.EntityId, ownerId);
                }
            }

            _lagTracker.Update(lags);

            // update owner -> laggiest grid map w/ latest state
            foreach (var trackedGrid in _lagTracker.GetTrackedEntities())
            {
                if (latestLaggiestGridToOwnerIds.TryGetValue(trackedGrid.Id, out var ownerId))
                {
                    _ownerToLaggiestGrids[ownerId] = trackedGrid.Id;
                }
            }

            var trackedGridIds = _lagTracker.GetTrackedEntityIds().ToSet();
            foreach (var (ownerId, laggiestGridId) in _ownerToLaggiestGrids.ToArray())
            {
                if (!trackedGridIds.Contains(laggiestGridId))
                {
                    _ownerToLaggiestGrids.Remove(ownerId);
                    Log.Trace($"removed untracked owner from owner map: {ownerId}");
                }
            }

            Log.Trace("updated grid lags");
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public IEnumerable<TrackedEntitySnapshot> GetPinnedGrids()
        {
            return _lagTracker.GetTopPins();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Clear()
        {
            _lagTracker.Clear();
            _ownerToLaggiestGrids.Clear();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void StopTracking(long gridId)
        {
            _lagTracker.StopTracking(gridId);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGetTimeSeries(long entityId, out ITimeSeries<double> timeSeries)
        {
            return _lagTracker.TryGetTimeSeries(entityId, out timeSeries);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public IEnumerable<TrackedEntitySnapshot> GetTrackedEntities()
        {
            return _lagTracker.GetTrackedEntities();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGetTrackedEntity(long entityId, out TrackedEntitySnapshot entity)
        {
            return _lagTracker.TryGetTrackedEntity(entityId, out entity);
        }

        public bool TryTraverseTrackedEntityByName(string name, out TrackedEntitySnapshot entity)
        {
            return _lagTracker.TryTraverseTrackedEntityByName(name, out entity);
        }
    }
}