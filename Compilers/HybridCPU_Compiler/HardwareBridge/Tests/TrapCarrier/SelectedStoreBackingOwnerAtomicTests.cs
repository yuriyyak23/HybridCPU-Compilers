using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core.Memory;

namespace HybridCPU_ISE.Tests.Architecture;

public sealed class SelectedStoreBackingOwnerAtomicTests
{
    private static readonly byte[] Data = { 0xA1, 0xB2, 0xC3, 0xD4 };
    private static readonly ulong?[] Domain = { 7UL };

    [Theory]
    [InlineData("success")]
    [InlineData("epoch")]
    [InlineData("owner")]
    [InlineData("layout")]
    [InlineData("domain")]
    [InlineData("bytes")]
    public void BufferedOwner_CommitIsAtomic(string conflict)
    {
        var owner = new BufferOwner();
        owner.AllocateMemory(0, 64);
        Assert.True(owner.TryWritePhysicalRange(8, new byte[] { 1, 2, 3, 4 }));
        var layout = Layout(8);
        var prepared = owner.Prepare(layout, Data, Domain);
        ulong epochBeforeAttempt = owner.ReplayRelevantMutationEpoch;
        byte[] before = Read(owner, 0, 64);
        var currentOwner = (Processor.MainMemoryArea)owner;
        var currentLayout = layout;
        byte[] currentBytes = Data;
        ulong?[] currentDomains = Domain;

        if (conflict == "epoch")
        {
            Assert.True(owner.TryWritePhysicalRange(32, new byte[] { 0xEE }));
            epochBeforeAttempt = owner.ReplayRelevantMutationEpoch;
            before = Read(owner, 0, 64);
        }
        else if (conflict == "owner") currentOwner = new Processor.MainMemoryArea();
        else if (conflict == "layout") currentLayout = Layout(9);
        else if (conflict == "domain") currentDomains = new ulong?[] { 8UL };
        else if (conflict == "bytes") currentBytes = new byte[] { 9, 9, 9, 9 };

        if (conflict == "success")
        {
            lock (owner) Assert.Equal(4, ((Processor.IPreparedSelectedStore)prepared).Commit(currentOwner, currentLayout, currentBytes, currentDomains));
            byte[] expected = (byte[])before.Clone();
            Data.CopyTo(expected.AsSpan(8, Data.Length));
            Assert.Equal(expected, Read(owner, 0, 64));
            Assert.Equal(epochBeforeAttempt + 1, owner.ReplayRelevantMutationEpoch);
            Assert.True(owner.LastProposal!.IsPublished);
        }
        else
        {
            Assert.Throws<IOException>(() => Commit(owner, prepared, currentOwner, currentLayout, currentBytes, currentDomains));
            Assert.Equal(before, Read(owner, 0, 64));
            Assert.Equal(epochBeforeAttempt, owner.ReplayRelevantMutationEpoch);
            Assert.False(owner.LastProposal!.IsPublished);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("epoch")]
    [InlineData("policy")]
    [InlineData("page-identity")]
    [InlineData("owner")]
    [InlineData("layout")]
    [InlineData("domain")]
    [InlineData("bytes")]
    public void PagedOwner_CommitIsAtomicAcrossPageBoundary(string conflict)
    {
        var owner = new PageOwner();
        owner.Seed(0, new byte[] { 10, 11, 12, 13, 14, 15, 16, 17 });
        owner.Seed(1, new byte[] { 20, 21, 22, 23, 24, 25, 26, 27 });
        var layout = Layout(6); // Four bytes span page 0 offsets 6..7 and page 1 offsets 0..1.
        var prepared = owner.Prepare(layout, Data, Domain);
        Dictionary<ulong, byte[]> oldPages = owner.PageReferences();
        byte[] before = owner.ReadWindow(6, 4);
        ulong epochBeforeAttempt = owner.ReplayRelevantMutationEpoch;
        var currentOwner = (Processor.MainMemoryArea)owner;
        var currentLayout = layout;
        byte[] currentBytes = Data;
        ulong?[] currentDomains = Domain;

        if (conflict == "epoch")
        {
            Assert.True(owner.TryWritePhysicalRange(32, new byte[] { 0xEE }));
            epochBeforeAttempt = owner.ReplayRelevantMutationEpoch;
        }
        else if (conflict == "policy")
        {
            owner.InvalidatePolicy();
            epochBeforeAttempt = owner.ReplayRelevantMutationEpoch;
        }
        else if (conflict == "page-identity") owner.ReplacePage(0);
        else if (conflict == "owner") currentOwner = new Processor.MainMemoryArea();
        else if (conflict == "layout") currentLayout = Layout(7);
        else if (conflict == "domain") currentDomains = new ulong?[] { 8UL };
        else if (conflict == "bytes") currentBytes = new byte[] { 9, 9, 9, 9 };

        if (conflict == "success")
        {
            lock (owner) Assert.Equal(4, prepared.Commit(currentOwner, currentLayout, currentBytes, currentDomains));
            Assert.Equal(new byte[] { 14, 15, 0xA1, 0xB2 }, owner.ReadWindow(4, 4));
            Assert.Equal(new byte[] { 0xC3, 0xD4, 22, 23 }, owner.ReadWindow(8, 4));
            Assert.Equal(epochBeforeAttempt + 1, owner.ReplayRelevantMutationEpoch);
            Assert.True(owner.LastProposal!.IsPublished);
            Assert.NotSame(oldPages[0], owner.PageReferences()[0]);
            Assert.NotSame(oldPages[1], owner.PageReferences()[1]);
        }
        else
        {
            Dictionary<ulong, byte[]> refsBeforeCommit = owner.PageReferences();
            var bytesBeforeCommit = refsBeforeCommit.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone());
            byte[] windowBeforeCommit = owner.ReadWindow(6, 4);
            Assert.Throws<IOException>(() => Commit(owner, prepared, currentOwner, currentLayout, currentBytes, currentDomains));
            Dictionary<ulong, byte[]> refsAfterCommit = owner.PageReferences();
            Assert.Equal(refsBeforeCommit.Keys.OrderBy(x => x), refsAfterCommit.Keys.OrderBy(x => x));
            foreach (ulong key in refsBeforeCommit.Keys)
            {
                Assert.Same(refsBeforeCommit[key], refsAfterCommit[key]);
                Assert.Equal(bytesBeforeCommit[key], refsAfterCommit[key]);
            }
            Assert.Equal(windowBeforeCommit, owner.ReadWindow(6, 4));
            Assert.Equal(epochBeforeAttempt, owner.ReplayRelevantMutationEpoch);
            Assert.False(owner.LastProposal!.IsPublished);
            Assert.Equal(before, owner.ReadWindow(6, 4));
        }
    }

    private static void Commit(Processor.MainMemoryArea owner, Processor.IPreparedSelectedStore prepared,
        Processor.MainMemoryArea currentOwner, CpuPhysicalRangeLayout layout, byte[] bytes,
        ulong?[] domains)
    { lock (owner) prepared.Commit(currentOwner, layout, bytes, domains); }

    private static byte[] Read(Processor.MainMemoryArea owner, ulong address, int length)
    { byte[] bytes = new byte[length]; Assert.True(owner.TryReadPhysicalRange(address, bytes)); return bytes; }

    private static CpuPhysicalRangeLayout Layout(ulong address) =>
        new(4, new[] { new CpuPhysicalRangeSegment(0, address, 4) }, translationGeneration: 3);

    private sealed class BufferOwner : Processor.BufferedSelectedStoreMemoryArea
    {
        internal Processor.SelectedStoreProposal? LastProposal { get; private set; }
        protected override bool AcceptSelectedStore(Processor.SelectedStoreProposal proposal) { LastProposal = proposal; return true; }
        internal Processor.BufferedSelectedStoreMemoryArea.PreparedStore Prepare(CpuPhysicalRangeLayout layout, byte[] bytes, ulong?[] domains) =>
            PrepareSelectedStore(layout, bytes, domains);
    }

    // Internal PreparedStore is wrapped to avoid exposing its nested internal type in test helpers.
    private sealed class PageOwner : Processor.PagedSelectedStoreMemoryArea
    {
        internal Processor.SelectedStoreProposal? LastProposal { get; private set; }
        protected override PreparedPagedStore? PrepareSelectedStore(Processor.SelectedStoreProposal proposal)
        { LastProposal = proposal; lock (this) return PreparePagedStore(proposal, SelectedStorePages, 8, 64, 64); }
        internal void Seed(ulong index, byte[] bytes) { lock (this) SelectedStorePages[index] = bytes; }
        internal void ReplacePage(ulong index) { lock (this) SelectedStorePages[index] = (byte[])SelectedStorePages[index].Clone(); }
        internal void InvalidatePolicy() { lock (this) InvalidatePreparedSelectedStores(); }
        internal Dictionary<ulong, byte[]> PageReferences() { lock (this) return SelectedStorePages.ToDictionary(p => p.Key, p => p.Value); }
        internal byte[] ReadWindow(ulong address, int length)
        {
            byte[] result = new byte[length];
            for (int i = 0; i < length; i++)
            {
                ulong current = address + (ulong)i;
                result[i] = SelectedStorePages[current / 8][current % 8];
            }
            return result;
        }
        internal Processor.IPreparedSelectedStore Prepare(CpuPhysicalRangeLayout layout, byte[] bytes, ulong?[] domains) =>
            PrepareSelectedStoreWindow(layout, bytes, domains);
    }
}
