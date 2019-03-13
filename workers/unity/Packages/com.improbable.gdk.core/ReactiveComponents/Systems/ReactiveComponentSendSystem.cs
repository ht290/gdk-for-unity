using System;
using System.Collections.Generic;
using System.Reflection;
using Improbable.Gdk.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine.Profiling;

namespace Improbable.Gdk.ReactiveComponents
{
    /// <summary>
    ///     Executes the default replication logic for each SpatialOS component.
    /// </summary>
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(SpatialOSSendGroup.InternalSpatialOSSendGroup))]
    [UpdateBefore(typeof(ComponentUpdateSystem))]
    public class ReactiveComponentSendSystem : ComponentSystem
    {
        private readonly List<ComponentReplicator> componentReplicators = new List<ComponentReplicator>();
        private NativeArray<ArchetypeChunk>[] chunkArrayCache;

        protected override void OnCreateManager()
        {
            base.OnCreateManager();

            PopulateDefaultComponentReplicators();
            chunkArrayCache = new NativeArray<ArchetypeChunk>[componentReplicators.Count * 2];
        }

        protected override void OnUpdate()
        {
            Profiler.BeginSample("GatherChunks");
            var gatheringJobs = new NativeArray<JobHandle>(componentReplicators.Count * 2, Allocator.Temp);
            for (var i = 0; i < componentReplicators.Count; i++)
            {
                var replicator = componentReplicators[i];
                var eventIndex = i;
                var commandIndex = componentReplicators.Count + i;

                chunkArrayCache[eventIndex] = replicator.EventGroup.CreateArchetypeChunkArray(Allocator.TempJob, out var eventJobHandle);
                chunkArrayCache[commandIndex] = replicator.CommandGroup.CreateArchetypeChunkArray(Allocator.TempJob, out var commandJobHandle);

                gatheringJobs[eventIndex] = eventJobHandle;
                gatheringJobs[commandIndex] = commandJobHandle;
            }

            JobHandle.CompleteAll(gatheringJobs);
            gatheringJobs.Dispose();
            Profiler.EndSample();

            ReplicateEvents();
            ReplicateCommands();
        }

        private void ReplicateEvents()
        {
            var componentUpdateSystem = World.GetExistingManager<ComponentUpdateSystem>();

            for (var i = 0; i < componentReplicators.Count; i++)
            {
                var eventIndex = i;
                Profiler.BeginSample("SendEvents");
                componentReplicators[i].Handler.SendEvents(chunkArrayCache[eventIndex], this, componentUpdateSystem);
                chunkArrayCache[eventIndex].Dispose();
                Profiler.EndSample();
            }
        }

        private void ReplicateCommands()
        {
            var commandSystem = World.GetExistingManager<CommandSystem>();

            for (var i = 0; i < componentReplicators.Count; i++)
            {
                var commandIndex = componentReplicators.Count + i;
                Profiler.BeginSample("SendCommands");
                componentReplicators[i].Handler.SendCommands(chunkArrayCache[commandIndex], this, commandSystem);
                chunkArrayCache[commandIndex].Dispose();
                Profiler.EndSample();
            }
        }

        internal void AddComponentReplicator(IReactiveComponentReplicationHandler reactiveComponentReplicationHandler)
        {
            componentReplicators.Add(new ComponentReplicator
            {
                Handler = reactiveComponentReplicationHandler,
                EventGroup = GetComponentGroup(reactiveComponentReplicationHandler.EventQuery),
                CommandGroup = GetComponentGroup(reactiveComponentReplicationHandler.CommandQueries),
            });
        }

        private void PopulateDefaultComponentReplicators()
        {
            // Find all component specific replicators and create an instance.
            var types = ReflectionUtility.GetNonAbstractTypes(typeof(IReactiveComponentReplicationHandler));

            foreach (var type in types)
            {
                if (type.GetCustomAttribute(typeof(DisableAutoRegisterAttribute)) != null)
                {
                    continue;
                }

                var componentReplicationHandler = (IReactiveComponentReplicationHandler) Activator.CreateInstance(type);
                AddComponentReplicator(componentReplicationHandler);
            }
        }

        private struct ComponentReplicator
        {
            public IReactiveComponentReplicationHandler Handler;
            public ComponentGroup EventGroup;
            public ComponentGroup CommandGroup;
        }
    }
}
