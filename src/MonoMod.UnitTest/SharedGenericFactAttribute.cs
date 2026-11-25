using MonoMod.Utils;
using Xunit;

namespace MonoMod.UnitTest
{
    public sealed class SharedGenericFactAttribute : FactAttribute
    {
        public SharedGenericFactAttribute()
        {
            if (PlatformDetection.Runtime is RuntimeKind.Mono)
            {
                Skip = "Detours of shared generic code are not supported on Mono";
            }
        }
    }
}
