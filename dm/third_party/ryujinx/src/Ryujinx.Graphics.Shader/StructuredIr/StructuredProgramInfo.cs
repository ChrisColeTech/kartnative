using System.Collections.Generic;

namespace Ryujinx.Graphics.Shader.StructuredIr
{
    class StructuredProgramInfo
    {
        public List<StructuredFunction> Functions { get; }

        public HashSet<IoDefinition> IoDefinitions { get; }

        public HelperFunctionsMask HelperFunctionsMask { get; set; }

        // nxnative: an operation accesses StorageKind.PhysicalMemory (Translation/Optimizations/GlobalPointers.cs)
        public bool UsesPhysicalMemory { get; set; }

        public StructuredProgramInfo()
        {
            Functions = new List<StructuredFunction>();

            IoDefinitions = new HashSet<IoDefinition>();
        }
    }
}
