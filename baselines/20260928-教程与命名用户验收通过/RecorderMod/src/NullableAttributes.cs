// Roslyn's SDK-free build uses the shipped runtime instead of reference packs.
// .NET 6 keeps these metadata attributes internal; embed the standard contract.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple=false, Inherited=false)]
    internal sealed class NullableAttribute : Attribute
    {
        public NullableAttribute(byte value) { }
        public NullableAttribute(byte[] value) { }
    }
    [AttributeUsage(AttributeTargets.All, AllowMultiple=false, Inherited=false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public NullableContextAttribute(byte value) { }
    }
}
