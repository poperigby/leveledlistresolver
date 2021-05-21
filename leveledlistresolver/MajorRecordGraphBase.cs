﻿using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Noggog;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace leveledlistresolver
{
    public abstract class MajorRecordGraphBase<TMod, TModGetter, TMajor, TMajorGetter>
        where TMod : class, IMod, TModGetter
        where TModGetter : class, IModGetter
        where TMajor : class, IMajorRecordCommon, TMajorGetter
        where TMajorGetter : class, IMajorRecordCommonGetter
    {
        protected readonly TMod patchMod;
        protected readonly GameRelease gameRelease;
        protected readonly ILinkCache<TMod, TModGetter> linkCache;

        public TMajorGetter Base { get; }
        public TMajorGetter ExtentBase { get; }
        public ModKey ModKey { get; }
        public FormKey FormKey { get => Base?.FormKey ?? FormKey.Null; }
        public ImmutableDictionary<ModKey, HashSet<ModKey>> Adjacents { get; }
        public ImmutableHashSet<TMajorGetter> ExtentRecords { get; }
        public bool IsInjected { get => ModKey != FormKey.ModKey; }

        public MajorRecordGraphBase(IPatcherState<TMod, TModGetter> state, in FormKey formKey)
        {
            patchMod = state.PatchMod;
            gameRelease = state.GameRelease;
            linkCache = state.LinkCache;

            var modContexts = linkCache.ResolveAllContexts<TMajor, TMajorGetter>(formKey).ToImmutableList().Reverse();
            var modKeys = modContexts.ConvertAll(static ctx => ctx.ModKey);

            var comparer = ModKey.LoadOrderComparer(modKeys);

            var contextDictionary = modContexts.ToImmutableSortedDictionary(ctx => ctx.ModKey, ctx => ctx.Record, comparer);
            var mastersDictionary = modKeys.SelectWhere<ModKey, TModGetter?>(state.LoadOrder.TryGetIfEnabledAndExists).NotNull()
                .ToDictionary(mod => mod.ModKey, mod => mod.MasterReferences.Select(refr => refr.Master).Intersect(modKeys).ToHashSet());

            ModKey = modContexts[0].ModKey;
            Base = contextDictionary[ModKey];

            var adjancentBuilder = modKeys.ToImmutableDictionary(key => key, key => new HashSet<ModKey>()).ToBuilder();

            foreach (var (modKey, masters) in mastersDictionary)
            {
                masters.UnionWith(masters.SelectMany(master => mastersDictionary[master]).ToHashSet());

                foreach (var master in masters)
                {
                    if (adjancentBuilder[master].Overlaps(masters) is false)
                    {
                        adjancentBuilder[master].Add(modKey);
                    }
                }
            }

            Adjacents = adjancentBuilder.ToImmutable();

            var extentMods = Adjacents.Where(kvp => kvp.Value is { Count: 0 } || kvp.Value.Contains(patchMod.ModKey)).Select(kvp => kvp.Key);
            var extentMasters = extentMods.Aggregate(modKeys, (list, modKey) => list.FindAll(key => mastersDictionary[modKey].Contains(key))).Sort(comparer);

            ExtentBase = extentMasters.IsEmpty ? Base : contextDictionary[extentMasters[^1]];
            ExtentRecords = extentMods.Select(modKey => contextDictionary[modKey]).ToImmutableHashSet();
            
            Console.WriteLine(Environment.NewLine + this);
        }

        public ushort GetFormVersion()
        {
            return gameRelease.GetDefaultFormVersion() ?? Base.FormVersion ?? throw RecordException.Enrich(new NullReferenceException("FormVersion was Null"), Base);
        }

        public uint GetVersionControl()
        {
            var formVersion = GetFormVersion();

            return formVersion switch
            {
                43 => _le(),
                44 => _sse(),
                _ => throw new NotImplementedException()
            };

            static uint _le()
            {
                return 0;
            }

            static uint _sse() {
                DateTime date = DateTime.Now;
                return Convert.ToUInt32(((date.Year - 2000) << 9) + (date.Month << 5) + date.Day);
            }          
        }

        public string GetEditorID()
        {
            return ExtentRecords.LastOrDefault(record => 
                (record.EditorID?.Equals(Base.EditorID, StringComparison.InvariantCulture) ?? false) is false
            )?.EditorID ?? Base.EditorID ?? Guid.NewGuid().ToString();
        }

        public override string ToString()
        {
            return ToString(ModKey);
        }

        public string ToString(in ModKey startPoint) 
        {
            if (Adjacents.ContainsKey(startPoint) is false)
                return string.Empty;

            string header = IsInjected ? $"{GetEditorID()} [{FormKey} | Injected by {ModKey}]": $"{GetEditorID()} [{FormKey}]";
            StringBuilder builder = new(header);

            var start = ImmutableList.Create(startPoint);
            var extents = Adjacents.Where(kvp => kvp.Value.Count is 0);

            foreach (var (extent, _) in extents)
                Visit(startPoint, extent, start);

            void Visit(in ModKey start, in ModKey end, ImmutableList<ModKey> visited)
            {
                if (start == end)
                {
                    builder.Append(Environment.NewLine + string.Join(" -> ", visited));
                    return;
                }
                else
                {
                    foreach (var node in Adjacents[start])
                    {
                        Visit(node, end, visited.Add(node));
                    }
                }
            }

            return builder.ToString();
        }

        public abstract MajorRecord ToMajorRecord();
    }
}
