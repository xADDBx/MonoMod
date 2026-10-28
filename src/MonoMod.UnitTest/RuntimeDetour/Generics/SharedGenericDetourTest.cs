using MonoMod.Core;
using MonoMod.Core.Platforms;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace MonoMod.UnitTest.RuntimeDetour.Generics
{
    [Collection("RuntimeDetour")]
    public class SharedGenericDetourTest(ITestOutputHelper helper) : TestBase(helper)
    {
        internal struct Big
        {
            public long A, B, C, D;
        }

        internal static class Methods
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string M<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static Big Many<T>(T value, int a, long b, double c, string d, Big e, int f)
                => new() { A = a, B = b, C = (long)c + d.Length, D = e.A + f + (value is null ? 0 : 1) };

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static long Tiered<T>(T value, int a, long b, double c, string d, int e) => a + b + (long)c + d.Length + e;
        }

        internal class Box<T>(T value)
        {
            public T Value = value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public Big Large(long x) => new() { A = x, B = Value is null ? 0 : 1 };

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static long Tiered(T value, int a, long b, double c, string d, int e) => a + b + (long)c + d.Length + e;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string MString(string value) => "hooked " + value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string MList(List<int> value) => "hooked list " + value.Count;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Big ManyString(string value, int a, long b, double c, string d, Big e, int f)
            => new() { A = a * 10, B = b * 10, C = (long)c * 10 + d.Length, D = e.D + f + value.Length };

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Big BoxLarge(Box<string> self, long x) => new() { A = x * 2, B = self.Value.Length, C = 7 };

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static long TieredString(string value, int a, long b, double c, string d, int e) => -1;

        private static MethodInfo Get(Type type, string name) => type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!;
        private static MethodInfo Target(string name) => Get(typeof(SharedGenericDetourTest), name);

        private static ICoreSharedGenericDetour Create(MethodBase closed, Action<MethodBase>? onUnknown = null)
        {
            var factory = (ISharedGenericDetourFactory)DetourFactory.Current;
            Assert.True(factory.TryGetSharedGenericForm(closed, out var form), $"{closed} doesn't share its body");
            return factory.CreateSharedGenericDetour(form!, onUnknown ?? (_ => { }));
        }

        [Fact]
        public void GenericMethod()
        {
            var closed = Get(typeof(Methods), nameof(Methods.M)).MakeGenericMethod(typeof(string));
            using (var detour = Create(closed))
            {
                detour.SetEntry(closed, Target(nameof(MString)));
                Assert.Equal("hooked a", Methods.M("a"));
                Assert.Equal("orig b", Methods.M<object>("b"));
                Assert.Equal("orig 1", Methods.M(1));

                detour.ClearEntry(closed);
                Assert.Equal("orig a", Methods.M("a"));
                detour.SetEntry(closed, Target(nameof(MString)));
            }
            Assert.Equal("orig a", Methods.M("a"));
        }

        [Fact]
        public void UnknownInstantiation()
        {
            var form = Get(typeof(Methods), nameof(Methods.M)).MakeGenericMethod(typeof(List<int>));
            var seen = new List<MethodBase>();
            ICoreSharedGenericDetour? self = null;
            using var detour = Create(form, closed =>
            {
                seen.Add(closed);
                if (closed.GetGenericArguments()[0] == typeof(List<int>))
                    self!.SetEntry(closed, Target(nameof(MList)));
            });
            self = detour;

            // the callback sets the entry before the first call is dispatched
            Assert.Equal("hooked list 2", Methods.M(new List<int> { 1, 2 }));
            Assert.Equal("orig x", Methods.M<object>("x"));
            Assert.Contains(seen, m => m.GetGenericArguments()[0] == typeof(object));
            var count = seen.Count;
            Assert.Equal("orig x", Methods.M<object>("x"));

            detour.ResetUnknownInstantiations();
            Assert.Equal("orig x", Methods.M<object>("x"));
            Assert.Equal(count + 1, seen.Count);
        }

        [Fact]
        public void ManyArgumentsAndReturnBuffer()
        {
            var closed = Get(typeof(Methods), nameof(Methods.Many)).MakeGenericMethod(typeof(string));
            using var detour = Create(closed);
            var e = new Big { A = 100, D = 1000 };
            detour.SetEntry(closed, Target(nameof(ManyString)));
            var hooked = Methods.Many("abc", 1, 2, 3.0, "de", e, 4);
            Assert.Equal((10L, 20L, 32L, 1007L), (hooked.A, hooked.B, hooked.C, hooked.D));
            var orig = Methods.Many<object>("abc", 1, 2, 3.0, "de", e, 4);
            Assert.Equal((1L, 2L, 5L, 105L), (orig.A, orig.B, orig.C, orig.D));
        }

        [Fact]
        public void InstanceMethodWithReturnBuffer()
        {
            var closed = Get(typeof(Box<string>), nameof(Box<string>.Large));
            using var detour = Create(closed);
            detour.SetEntry(closed, Target(nameof(BoxLarge)));
            var hooked = new Box<string>("abc").Large(5);
            Assert.Equal((10L, 3L, 7L), (hooked.A, hooked.B, hooked.C));
            var orig = new Box<object>("abc").Large(5);
            Assert.Equal((5L, 1L, 0L), (orig.A, orig.B, orig.C));
        }

        // many arguments make CoreCLR use IL instantiating stubs, which body walking can't see through
        [Fact]
        public void PrecompiledAndTieredBodies()
        {
            Assert.Equal(16, Methods.Tiered("a", 1, 2, 3, "de", 8));
            Assert.Equal(16, Box<string>.Tiered("a", 1, 2, 3, "de", 8));

            var method = Get(typeof(Methods), nameof(Methods.Tiered)).MakeGenericMethod(typeof(string));
            var onType = Get(typeof(Box<string>), nameof(Box<string>.Tiered));
            using var methodDetour = Create(method);
            using var typeDetour = Create(onType);
            methodDetour.SetEntry(method, Target(nameof(TieredString)));
            typeDetour.SetEntry(onType, Target(nameof(TieredString)));

            // .NET Core 3.0 turned tiering on; 2.1 misreports its version but has no System.Index. Unoptimized (Debug) code doesn't tier.
            var tiers = PlatformDetection.Runtime is RuntimeKind.CoreCLR && typeof(object).Assembly.GetType("System.Index") is not null
                && typeof(SharedGenericDetourTest).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;
            var recompiled = new HashSet<Type>();
            OnMethodCompiledCallback onCompiled = (_, compiled, _, _, _) =>
            {
                if (compiled?.Name == nameof(Methods.Tiered))
                {
                    lock (recompiled)
                        recompiled.Add(compiled.DeclaringType!);
                }
            };
            PlatformTriple.Current.Runtime.OnMethodCompiled += onCompiled;
            try
            {
                // tiering waits while other methods are still being compiled, which takes a while on a busy machine;
                // the round after the recompilations runs the recompiled bodies
                var total = Stopwatch.StartNew();
                bool lastRound;
                do
                {
                    lastRound = !tiers || RecompiledCount() == 2 || total.Elapsed.TotalSeconds >= 60;
                    for (var i = 0; i < 200; i++)
                    {
                        Assert.Equal(-1, Methods.Tiered("a", 1, 2, 3, "de", 8));
                        Assert.Equal(16, Methods.Tiered<object>("a", 1, 2, 3, "de", 8));
                        Assert.Equal(-1, Box<string>.Tiered("a", 1, 2, 3, "de", 8));
                        Assert.Equal(16, Box<object>.Tiered("a", 1, 2, 3, "de", 8));
                    }
                    if (!lastRound)
                        Thread.Sleep(50);
                }
                while (!lastRound);
            }
            finally
            {
                PlatformTriple.Current.Runtime.OnMethodCompiled -= onCompiled;
            }
            Assert.True(!tiers || RecompiledCount() == 2, "the shared bodies weren't recompiled by tiered compilation");

            int RecompiledCount()
            {
                lock (recompiled)
                    return recompiled.Count;
            }
        }
    }
}
