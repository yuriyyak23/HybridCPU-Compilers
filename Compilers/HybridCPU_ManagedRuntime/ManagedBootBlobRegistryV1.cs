using System.Buffers.Binary;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCPU.ManagedRuntime;

/// <summary>
/// Loader-owned registry for already materialized immutable managed byte arrays. It never copies
/// blob bytes and keeps every accepted object as a process-lifetime handle root.
/// </summary>
public sealed class HybridCpuManagedBootBlobRegistryV1 : IHybridCpuManagedBootBlobProviderV1
{
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapAllocatorV1 _heap;
    private readonly HybridCpuManagedGcRootRegistryV1 _roots;
    private readonly SortedDictionary<int,(ulong Reference,int Length,string Digest)> _blobs=[];

    public HybridCpuManagedBootBlobRegistryV1(HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedHeapAllocatorV1 heap,HybridCpuManagedGcRootRegistryV1 roots)
    { _types=types??throw new ArgumentNullException(nameof(types));_heap=heap??throw new ArgumentNullException(nameof(heap));_roots=roots??throw new ArgumentNullException(nameof(roots)); }

    public bool Register(int blobId,ulong managedReference)
    {
        if(blobId is <=0 or > HybridCpuBootBlobServiceContractV1.MaximumBlobId || managedReference==0 ||
           !_heap.TryGetAllocation(managedReference,out HybridCpuManagedActiveAllocationV1? allocation) || allocation is null ||
           _types.Resolve(allocation.TypeId) is not { Kind:HybridCpuManagedTypeKindV1.SzArray,
               ArrayShape:{IsSzArray:true,ElementStorageKind:HybridCpuManagedStorageKindV1.Primitive,
                   ElementSizeBytes:1,ElementAlignmentBytes:1} shape } ||
           shape.LengthOffsetBytes>allocation.ObjectSizeBytes-sizeof(int) || shape.DataOffsetBytes<shape.LengthOffsetBytes+sizeof(int)) return false;
        Span<byte> lengthBytes=stackalloc byte[4];
        if(!_heap.TryReadObjectBytes(managedReference,shape.LengthOffsetBytes,lengthBytes)) return false;
        int length=BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if(length<=0 || length>HybridCpuBootBlobServiceContractV1.MaximumBlobBytes ||
           shape.DataOffsetBytes>allocation.ObjectSizeBytes-length) return false;
        string digest=HybridCpuPlatformContractV1.Hash(string.Join('|',HybridCpuBootBlobServiceContractV1.ContractDigest,
            blobId,managedReference,length,allocation.TypeId,allocation.TypeDescriptorDigest));
        if(_blobs.TryGetValue(blobId,out (ulong Reference, int Length, string Digest) existing)) return existing.Reference==managedReference&&existing.Length==length;
        string rootIdentity=$"boot-blob:{blobId}";
        if(!_roots.Register(rootIdentity,HybridCpuManagedGcRootSourceV1.Handle,managedReference)) return false;
        _blobs.Add(blobId,(managedReference,length,digest)); return true;
    }

    public HybridCpuManagedBootBlobProviderResultV1 Resolve(int blobId)=>
        _blobs.TryGetValue(blobId,out (ulong Reference, int Length, string Digest) blob)
            ? new(true,blob.Reference,blob.Length,blob.Digest,string.Empty)
            : new(false,0,0,string.Empty,"The requested immutable boot blob is not registered.");
}
