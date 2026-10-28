using MonoMod.Core;
using MonoMod.Core.Platforms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace MonoMod.UnitTest.RuntimeDetour.Generics
{
    // CoreCLR and Framework share anything with a reference type argument through __Canon, Mono only all-reference instantiations
    [Collection("RuntimeDetour")]
    public class GenericSharingProbeTest(ITestOutputHelper helper) : TestBase(helper)
    {
        private readonly ITestOutputHelper output = helper;

#pragma warning disable CS0649, CA1812 // only used as type arguments
        internal enum IntLike { A }
        internal struct SmallStruct { public int X; }
        internal struct RefStruct { public string S; }

        internal static class Probe
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static int M<T>(T value) => value?.GetHashCode() ?? 0;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static int M2<T1, T2>(T1 a, T2 b) => (a?.GetHashCode() ?? 0) ^ (b?.GetHashCode() ?? 0);
        }

        internal class ProbeClass<T>
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public int Inst(T value) => value?.GetHashCode() ?? 0;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static int Static(T value) => value?.GetHashCode() ?? 0;
        }
#pragma warning restore CS0649, CA1812

        private static readonly MethodInfo m = typeof(Probe).GetMethod(nameof(Probe.M))!;
        private static readonly MethodInfo m2 = typeof(Probe).GetMethod(nameof(Probe.M2))!;

        private static MethodInfo Inst(Type t) => typeof(ProbeClass<>).MakeGenericType(t).GetMethod(nameof(ProbeClass<int>.Inst))!;
        private static MethodInfo Static(Type t) => typeof(ProbeClass<>).MakeGenericType(t).GetMethod(nameof(ProbeClass<int>.Static))!;

        [Fact]
        public void SharedBodies()
        {
            var targets = new List<MethodBase>();
            foreach (var t in new[] { typeof(int), typeof(long), typeof(IntLike), typeof(SmallStruct), typeof(RefStruct), typeof(string), typeof(object), typeof(KeyValuePair<int, string>), typeof(KeyValuePair<int, object>) })
                targets.Add(m.MakeGenericMethod(t));
            foreach (var (a, b) in new[] { (typeof(int), typeof(string)), (typeof(int), typeof(object)), (typeof(string), typeof(int)), (typeof(string), typeof(object)), (typeof(int), typeof(long)) })
                targets.Add(m2.MakeGenericMethod(a, b));
            foreach (var t in new[] { typeof(int), typeof(string), typeof(object) })
            {
                targets.Add(Inst(t));
                targets.Add(Static(t));
            }

            var triple = PlatformTriple.Current;
            output.WriteLine($"Runtime: {triple.HostTriple}, {RuntimeInformation.FrameworkDescription}");

            var factory = DetourFactory.Current as ISharedGenericDetourFactory;
            var forms = new Dictionary<MethodBase, MethodBase?>();
            var bodies = new Dictionary<MethodBase, IntPtr>();
            foreach (var target in targets)
            {
                var form = forms[target] = factory is not null && factory.TryGetSharedGenericForm(target, out var f) ? f : null;
                // compiled first, like detours do: the body walk can't recognize ThePreStub everywhere (arm64 before .NET 7)
                triple.Compile(form ?? target);
                var body = bodies[target] = triple.GetNativeMethodBody(form ?? target);
                var args = target.IsGenericMethod ? target.GetGenericArguments() : target.DeclaringType!.GetGenericArguments();
                output.WriteLine($"{target.DeclaringType!.Name}.{target.Name}<{string.Join(",", args.Select(t => t.Name))}>".PadRight(40) + $" 0x{body:x} {form}");
            }

            // the shared form is the same object for every instantiation of a body
            foreach (var target in targets.Where(t => forms[t] is not null))
            {
                Assert.All(targets.Where(t => forms[t] is not null && bodies[t] == bodies[target]), t => Assert.Same(forms[target], forms[t]));
            }

            IntPtr M(params Type[] args) => bodies[args.Length == 1 ? m.MakeGenericMethod(args) : m2.MakeGenericMethod(args)];

            Assert.Equal(M(typeof(string)), M(typeof(object)));
            Assert.NotNull(forms[m.MakeGenericMethod(typeof(string))]);
            Assert.Equal(bodies[Inst(typeof(string))], bodies[Inst(typeof(object))]);
            Assert.Equal(bodies[Static(typeof(string))], bodies[Static(typeof(object))]);

            // not even with an enum of the same underlying type
            Assert.NotEqual(M(typeof(int)), M(typeof(long)));
            Assert.NotEqual(M(typeof(int)), M(typeof(IntLike)));
            Assert.NotEqual(bodies[Inst(typeof(int))], bodies[Inst(typeof(string))]);

            // M2<int, __Canon> on CoreCLR and Framework, Mono doesn't do partial sharing; the shared form must agree either way
            Assert.Equal(M(typeof(int), typeof(string)) == M(typeof(int), typeof(object)), forms[m2.MakeGenericMethod(typeof(int), typeof(string))] is not null);
            Assert.Equal(M(typeof(KeyValuePair<int, string>)) == M(typeof(KeyValuePair<int, object>)), forms[m.MakeGenericMethod(typeof(KeyValuePair<int, string>))] is not null);
            Assert.Null(forms[m.MakeGenericMethod(typeof(int))]);
        }
    }
}
