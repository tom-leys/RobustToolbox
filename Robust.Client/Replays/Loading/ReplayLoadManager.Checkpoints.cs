using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using NetSerializer;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using System.Threading.Tasks;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Replays;
using Robust.Shared.Upload;
using static Robust.Shared.Replays.ReplayMessage;

namespace Robust.Client.Replays.Loading;

// This partial class contains functions for generating "checkpoint" states, which are basically just full states that
// allow the client to jump to some point in time without having to re-process the whole replay up to that point. I.e.,
// so that when jumping to tick 1001 the client only has to apply states for tick 1000 and 1001, instead of 0, 1, 2, ...
public sealed partial class ReplayLoadManager
{
    public struct ReplayTickData(int stateId, GameState state, ReplayMessage messages, int progress, int maxProgress)
    {
        public readonly int StateId = stateId;
        public readonly GameState State = state;
        public readonly ReplayMessage Messages = messages;
        public readonly int Progress = progress;
        public readonly int MaxProgress = maxProgress;
    }

    public async Task<(CheckpointState[], TimeSpan[])> GenerateCheckpointsAsync(
        ReplayMessage? initMessages,
        HashSet<string> initialCvars,
        ChannelReader<ReplayTickData> states,
        LoadReplayCallback callback,
        LoadReplayJob? job)
    {
        // Given a set of states [0 to X], [X to X+1], [X+1 to X+2]..., this method  will generate additional states
        // like [0 to x+60 ], [0 to x+120], etc. This will make scrubbing/jumping to a state much faster, but requires
        // some pre-processing all of the states.
        //
        // This whole mess of a function uses a painful amount of LINQ conversion. but sadly the networked data is
        // generally sent as a list of values, which makes sense if the list contains simple state delta data that all
        // needs to be applied. But here we need to inspect existing states and combine/merge them, so things generally
        // need to be converted into a dictionary. But even with that requirement there are a bunch of performance
        // improvements to be made even without just de-LINQuifing or changing the networked data.
        //
        // Profiling with a 10 minute, 80-player replay, this function is about 50% entity spawning and 50% MergeState()
        // & array copying. It only takes ~3 seconds on my machine, so optimising it might not be necessary, but there
        // is still some low-hanging fruit, like:
        // TODO REPLAYS serialize checkpoints after first loading a replay so they only need to be generated once.
        //
        // TODO REPLAYS Add dynamic checkpoints.
        // If we end up using long (e.g., 5 minute) checkpoint intervals, that might still mean that scrubbing/rewinding
        // short time periods will be super stuttery. So its probably worth keeping a dynamic checkpoint following the
        // users current tick. E.g. while a replay is being replayed, keep a dynamic checkpoint that is ~30 secs behind
        // the current tick. that way the user can always go back up to ~30 seconds without having to go back to the
        // last checkpoint.
        //
        // Alternatively maybe just generate reverse states? I.e. states containing data that is required to go from
        // tick X to X-1? (currently any ent that had any changes will reset ALL of its components, not just the states
        // that actually need resetting. basically: iterate forwards though states. anytime a new  comp state gets
        // applied, for the reverse state simply add the previously applied component state.

        _sawmill.Info($"Begin checkpoint generation");
        var st = new Stopwatch();
        st.Start();

        Dictionary<string, object> cvars = new();
        foreach (var cvar in initialCvars)
        {
            cvars[cvar] = _confMan.GetCVar<object>(cvar);
        }

        var timeBase = _timing.TimeBase;
        var checkPoints = new List<CheckpointState>(1 + states.Count / _checkpointInterval);

        // Get all initial prototypes
        var prototypes = new Dictionary<Type, HashSet<string>>();
        foreach (var kindName in _protoMan.GetPrototypeKinds())
        {
            var kind = _protoMan.GetKindType(kindName);
            var set = new HashSet<string>();
            prototypes[kind] = set;
            foreach (var proto in _protoMan.EnumeratePrototypes(kind))
            {
                set.Add(proto.ID);
            }
        }

        HashSet<ResPath> uploadedFiles = new();
        var detached = new HashSet<NetEntity>();
        var detachQueue = new Dictionary<GameTick, List<NetEntity>>();

        var firstTask = states.ReadAsync();
        if (job != null)
        {
            firstTask = job.WaitAsyncTask(firstTask);
        }
        var firstData = await firstTask;
        await callback(firstData.Progress, firstData.MaxProgress, LoadingState.ProcessingFiles, false);
        if (initMessages != null)
            UpdateMessages(initMessages, uploadedFiles, prototypes, cvars, detachQueue, ref timeBase, true);
        UpdateMessages(firstData.Messages, uploadedFiles, prototypes, cvars, detachQueue, ref timeBase, true);

        var entSpan = firstData.State.EntityStates.Value;
        Dictionary<NetEntity, EntityState> entStates = new(entSpan.Count);
        foreach (var entState in entSpan)
        {
            var modifiedState = AddImplicitData(entState);
            entStates.Add(entState.NetEntity, modifiedState);
        }

        ProcessQueue(GameTick.MaxValue, detachQueue, detached, entStates);

        var playerSpan = firstData.State.PlayerStates.Value;
        Dictionary<NetUserId, SessionState> playerStates = new(playerSpan.Count);
        foreach (var player in playerSpan)
        {
            playerStates.Add(player.UserId, player);
        }

        var state0 = new GameState(GameTick.Zero,
            firstData.State.ToSequence,
            default,
            entStates.Values.ToArray(),
            playerStates.Values.ToArray(),
            Array.Empty<NetEntity>());
        checkPoints.Add(new CheckpointState(state0, timeBase, cvars, 0, detached));

        DebugTools.Assert(state0.EntityDeletions.Value.Count == 0);
        var empty = Array.Empty<NetEntity>();

        TimeSpan GetTime(GameTick tick)
        {
            var rate = (int) cvars[CVars.NetTickrate.Name];
            var period = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / rate);
            return timeBase.Item1 + (tick.Value - timeBase.Item2.Value) * period;
        }

        var serverTime = new List<TimeSpan> { TimeSpan.Zero };
        var initialTime = GetTime(state0.ToSequence);

        var ticksSinceLastCheckpoint = 0;
        var spawnedTracker = 0;
        var stateTracker = 0;
        var curState = state0;
        var lastStateId = 0;
        var stateEnumerator = states.ReadAllAsync();
        if (job != null)
        {
            stateEnumerator = job.WrapAsyncEnumerator(stateEnumerator);
        }
        await foreach (var state in stateEnumerator)
        {
            if (state.StateId % 10 == 0)
                await callback(state.Progress, state.MaxProgress, LoadingState.ProcessingFiles, false);

            DebugTools.Assert(state.State.FromSequence <= curState.ToSequence);
            lastStateId = state.StateId;
            curState = state.State;

            UpdatePlayerStates(curState.PlayerStates.Span, playerStates);
            UpdateEntityStates(curState.EntityStates.Span,
                entStates,
                ref spawnedTracker,
                ref stateTracker,
                detached);
            UpdateMessages(state.Messages, uploadedFiles, prototypes, cvars, detachQueue, ref timeBase);
            ProcessQueue(curState.ToSequence, detachQueue, detached, entStates);
            UpdateDeletions(curState.EntityDeletions, entStates, detached);
            serverTime.Add(GetTime(curState.ToSequence) - initialTime);
            ticksSinceLastCheckpoint++;

            if (ticksSinceLastCheckpoint < _checkpointInterval
                && spawnedTracker < _checkpointEntitySpawnThreshold
                && stateTracker < _checkpointEntityStateThreshold)
            {
                continue;
            }

            ticksSinceLastCheckpoint = 0;
            spawnedTracker = 0;
            stateTracker = 0;
            var newState = new GameState(GameTick.Zero,
                curState.ToSequence,
                default,
                entStates.Values.ToArray(),
                playerStates.Values.ToArray(),
                empty); // for full states, deletions are implicit by simply not being in the state
            checkPoints.Add(new CheckpointState(newState, timeBase, cvars, state.StateId, detached));
        }

        _sawmill.Info($"Finished generating {checkPoints.Count} checkpoints. Elapsed time: {st.Elapsed}. Checkpoint every {(float)lastStateId / checkPoints.Count} ticks on average");
        await callback(10000, 10000, LoadingState.ProcessingFiles, false);
        return (checkPoints.ToArray(), serverTime.ToArray());
    }

    private void ProcessQueue(
        GameTick curTick,
        Dictionary<GameTick, List<NetEntity>> detachQueue,
        HashSet<NetEntity> detached,
        Dictionary<NetEntity, EntityState> entStates)
    {
        foreach (var (tick, ents) in detachQueue)
        {
            if (tick > curTick)
                continue;
            detachQueue.Remove(tick);

            foreach (var e in ents)
            {
                if (entStates.ContainsKey(e))
                    detached.Add(e);
                else
                {
                    // AFAIK this should only happen if the client skipped over some ticks, probably due to packet loss
                    // I.e., entity was created on tick n, then leaves PVS range on the tick n+1
                    // If the n-th tick gets dropped, the client only ever receives the pvs-leave message.
                    // In that case we should just ignore it.
                    _sawmill.Debug($"Received a PVS detach msg for entity {e} before it was received?");
                }
            }
        }
    }

    private void UpdateMessages(ReplayMessage message,
        HashSet<ResPath> uploadedFiles,
        Dictionary<Type, HashSet<string>> prototypes,
        Dictionary<string, object> cvars,
        Dictionary<GameTick, List<NetEntity>> detachQueue,
        ref (TimeSpan, GameTick) timeBase,
        bool ignoreDuplicates = false)
    {
        for (var i = message.Messages.Count - 1; i >= 0; i--)
        {
            switch (message.Messages[i])
            {
                case CvarChangeMsg cvar:
                    foreach (var (name, value) in cvar.ReplicatedCvars)
                    {
                        cvars[name] = value;
                    }

                    timeBase = cvar.TimeBase;
                    break;

                case SharedNetworkResourceManager.ReplayResourceUploadMsg resUpload:

                    var path = resUpload.RelativePath.Clean().ToRelativePath();
                    if (uploadedFiles.Add(path) && !_netResMan.FileExists(path))
                    {
                        _netMan.DispatchLocalNetMessage(new NetworkResourceUploadMessage
                        {
                            RelativePath = path, Data = resUpload.Data
                        });
                        message.Messages.RemoveSwap(i);
                        break;
                    }

                    // Supporting this requires allowing files to track their last-modified time and making
                    // checkpoints reset files when jumping back, and applying all previous changes when jumping
                    // forwards. Also, note that files HAVE to be uploaded while generating checkpoints, in case
                    // someone spawns an entity that relies on uploaded data.
                    if (!ignoreDuplicates)
                    {
                        var msg = $"Overwriting an existing file upload! Path: {path}";
                        if (_confMan.GetCVar(CVars.ReplayIgnoreErrors))
                            _sawmill.Error(msg);
                        else
                            throw new NotSupportedException(msg);
                    }

                    message.Messages.RemoveSwap(i);
                    break;

                case LeavePvs leave:
                    detachQueue.TryAdd(leave.Tick, leave.Entities);
                    break;
            }
        }

        // Process prototype uploads **after** resource uploads.
        for (var i = message.Messages.Count - 1; i >= 0; i--)
        {
            if (message.Messages[i] is not ReplayPrototypeUploadMsg protoUpload)
                continue;

            message.Messages.RemoveSwap(i);

            try
            {
                LoadPrototype(protoUpload.PrototypeData, prototypes, ignoreDuplicates);
            }
            catch (Exception e)
            {
                if (e is NotSupportedException || !_confMan.GetCVar(CVars.ReplayIgnoreErrors))
                    throw;

                var msg = $"Caught exception while parsing uploaded prototypes in a replay. Exception: {e}";
                _sawmill.Error(msg);
            }
        }
    }

    private void LoadPrototype(
        string data,
        Dictionary<Type, HashSet<string>> prototypes,
        bool ignoreDuplicates)
    {
        var changed = new Dictionary<Type, HashSet<string>>();
        _protoMan.LoadString(data, true, changed);

        foreach (var (kind, ids) in changed)
        {
            var protos = prototypes[kind];
            var count = protos.Count;
            protos.UnionWith(ids);
            if (!ignoreDuplicates && ids.Count + count != protos.Count)
            {
                // An existing prototype was overwritten. Much like for resource uploading, supporting this
                // requires tracking the last-modified time of prototypes and either resetting or applying
                // prototype changes when jumping around in time. This also requires reworking how the initial
                // implicit state data is generated, because we can't simply cache it anymore.
                // Also, does reloading prototypes in release mode modify existing entities?

                var msg = $"Overwriting an existing prototype! Kind: {kind.Name}. Ids: {string.Join(", ", ids)}";
                if (_confMan.GetCVar(CVars.ReplayIgnoreErrors))
                    _sawmill.Error(msg);
                else
                    throw new NotSupportedException(msg);
            }
        }

        _protoMan.ResolveResults();
        _protoMan.ReloadPrototypes(changed);
        _locMan.ReloadLocalizations();
    }

    private void UpdateDeletions(NetListAsArray<NetEntity> entityDeletions,
        Dictionary<NetEntity, EntityState> entStates, HashSet<NetEntity> detached)
    {
        foreach (var ent in entityDeletions.Span)
        {
            entStates.Remove(ent);
            detached.Remove(ent);
        }
    }

    private void UpdateEntityStates(ReadOnlySpan<EntityState> span, Dictionary<NetEntity, EntityState> entStates,
        ref int spawnedTracker, ref int stateTracker, HashSet<NetEntity> detached)
    {
        foreach (var entState in span)
        {
            detached.Remove(entState.NetEntity);
            if (!entStates.TryGetValue(entState.NetEntity, out var oldEntState))
            {
                var modifiedState = AddImplicitData(entState);
                entStates[entState.NetEntity] = modifiedState;
                spawnedTracker++;

#if DEBUG
                foreach (var state in modifiedState.ComponentChanges.Value)
                {
                    DebugTools.Assert(state.State is not IComponentDeltaState delta || delta.FullState);
                }
#endif
                continue;
            }

            stateTracker++;
            DebugTools.Assert(oldEntState.NetEntity == entState.NetEntity);
            entStates[entState.NetEntity] = MergeStates(entState, oldEntState.ComponentChanges.Value, oldEntState.NetComponents);

#if DEBUG
            foreach (var state in entStates[entState.NetEntity].ComponentChanges.Span)
            {
                DebugTools.Assert(state.State is not IComponentDeltaState delta || delta.FullState);
            }
#endif
        }
    }

    private EntityState MergeStates(
        EntityState newState,
        IReadOnlyCollection<ComponentChange> oldState,
        HashSet<ushort>? oldNetComps)
    {
        var combined = oldState.ToList();
        var newCompStates = newState.ComponentChanges.Value.ToDictionary(x => x.NetID);

        // remove any deleted components
        if (newState.NetComponents != null)
        {
            for (var index = combined.Count - 1; index >= 0; index--)
            {
                if (!newState.NetComponents.Contains(combined[index].NetID))
                    combined.RemoveSwap(index);
            }
        }

        for (var index = combined.Count - 1; index >= 0; index--)
        {
            var existing = combined[index];

            if (!newCompStates.Remove(existing.NetID, out var newCompState))
                continue;

            if (newCompState.State is not IComponentDeltaState delta || delta.FullState)
            {
                combined[index] = newCompState;
                continue;
            }

            DebugTools.Assert(existing.State is IComponentDeltaState fullDelta && fullDelta.FullState);
            combined[index] = new ComponentChange(existing.NetID, delta.CreateNewFullState(existing.State), newCompState.LastModifiedTick);
        }

        foreach (var compChange in newCompStates.Values)
        {
            // I'm not 100% sure about this, but I think delta states should always be full states here?
            DebugTools.Assert(compChange.State is not IComponentDeltaState delta || delta.FullState);
            combined.Add(compChange);
        }

        DebugTools.Assert(newState.NetComponents == null || newState.NetComponents.Count == combined.Count);
        return new EntityState(newState.NetEntity, combined, newState.EntityLastModified, newState.NetComponents ?? oldNetComps);
    }

    private void UpdatePlayerStates(ReadOnlySpan<SessionState> span, Dictionary<NetUserId, SessionState> playerStates)
    {
        foreach (var player in span)
        {
            playerStates[player.UserId] = player;
        }
    }
}
