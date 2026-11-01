namespace Ryujinx.Graphics.Shader.IntermediateRepresentation
{
    enum StorageKind
    {
        None,
        Input,
        InputPerPatch,
        Output,
        OutputPerPatch,
        ConstantBuffer,
        StorageBuffer,
        LocalMemory,
        SharedMemory,
        SharedMemory8, // TODO: Remove this and store type as a field on the Operation class itself.
        SharedMemory16, // TODO: Remove this and store type as a field on the Operation class itself.
        GlobalMemory,
        GlobalMemoryS8, // TODO: Remove this and store type as a field on the Operation class itself.
        GlobalMemoryS16, // TODO: Remove this and store type as a field on the Operation class itself.
        GlobalMemoryU8, // TODO: Remove this and store type as a field on the Operation class itself.
        GlobalMemoryU16, // TODO: Remove this and store type as a field on the Operation class itself.
        // nxnative: a 32-bit word at a host GPU address, sources (address low, address high[, compare], [value]): the translation of a global memory
        // access through a pointer the program read from memory, after the host's address table gave the guest address its host address (GlobalPointers)
        PhysicalMemory,
    }

    static class StorageKindExtensions
    {
        public static bool IsInputOrOutput(this StorageKind storageKind)
        {
            return storageKind is StorageKind.Input or
                   StorageKind.InputPerPatch or
                   StorageKind.Output or
                   StorageKind.OutputPerPatch;
        }

        public static bool IsOutput(this StorageKind storageKind)
        {
            return storageKind is StorageKind.Output or
                   StorageKind.OutputPerPatch;
        }

        public static bool IsPerPatch(this StorageKind storageKind)
        {
            return storageKind is StorageKind.InputPerPatch or
                   StorageKind.OutputPerPatch;
        }
    }
}
