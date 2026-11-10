using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private sealed class ManagedModuleContext : IDisposable
    {
        private readonly MemoryStream _stream;
        public ManagedModuleContext(ReadOnlyMemory<byte> peImage, string sourceIdentity)
        {
            if (peImage.IsEmpty || string.IsNullOrWhiteSpace(sourceIdentity))
                throw new ArgumentException("Managed body-world module identity and PE image are required.");
            PeImage = peImage;
            SourceIdentity = sourceIdentity;
            ImageDigest = HashBytes(peImage.Span);
            _stream = new MemoryStream(peImage.ToArray(), writable: false);
            PEReader? reader = null;
            try
            {
                Reader = reader = new PEReader(_stream, PEStreamOptions.LeaveOpen);
                if (Reader.HasMetadata)
                {
                    Metadata = Reader.GetMetadataReader();
                    AssemblyName = Metadata.GetString(Metadata.GetAssemblyDefinition().Name);
                }
                else
                {
                    Metadata = null!;
                    AssemblyName = string.Empty;
                }
            }
            catch
            {
                reader?.Dispose();
                _stream.Dispose();
                throw;
            }
        }
        public ReadOnlyMemory<byte> PeImage { get; }
        public string SourceIdentity { get; }
        public string ImageDigest { get; }
        public string AssemblyName { get; }
        public PEReader Reader { get; }
        public MetadataReader Metadata { get; }
        public void Dispose()
        {
            Reader.Dispose();
            _stream.Dispose();
        }
    }

    private sealed class ManagedModuleContextCollection : IDisposable
    {
        public ManagedModuleContextCollection(IEnumerable<ManagedModuleContext> modules)
            : this(modules, CancellationToken.None) { }

        public ManagedModuleContextCollection(IEnumerable<ManagedModuleContext> modules, CancellationToken cancellationToken)
        {
            List<ManagedModuleContext> acquired = new List<ManagedModuleContext>();
            try
            {
                using IEnumerator<ManagedModuleContext> enumerator = modules.GetEnumerator();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!enumerator.MoveNext()) break;
                    acquired.Add(enumerator.Current);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                cancellationToken.ThrowIfCancellationRequested();
                Modules = acquired.ToArray();
            }
            catch
            {
                foreach (ManagedModuleContext module in acquired) module.Dispose();
                throw;
            }
        }
        public ManagedModuleContext[] Modules { get; }
        public void Dispose()
        {
            foreach (ManagedModuleContext module in Modules) module.Dispose();
        }
    }
}
