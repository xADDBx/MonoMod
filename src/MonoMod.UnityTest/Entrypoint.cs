using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Doorstop
{
    public static class Entrypoint
    {
        public static void Start()
            => new Thread(Run) { IsBackground = true, Name = "MonoMod tests" }.Start();

        private static void Run()
        {
            var exitCode = 2;
            try
            {
                WaitForUnity();
                var testDirectory = Path.GetDirectoryName(typeof(Entrypoint).Assembly.Location)
                    ?? throw new InvalidOperationException("Could not locate the test directory.");
                var testAssembly = Path.Combine(testDirectory, "MonoMod.UnitTest.dll");
                var outputPath = Environment.GetEnvironmentVariable("MONOMOD_UNITY_TEST_RESULTS")
                    ?? Path.Combine(Environment.CurrentDirectory, "unity-test-results.xml");
                var runner = Assembly.LoadFrom(Path.Combine(testDirectory, "xunit.console.exe"));
                var main = runner.EntryPoint ?? throw new MissingMethodException("xunit.console.exe", "Main");
                exitCode = (int)(main.Invoke(null,
                [
                    new[]
                    {
                        testAssembly,
                        "-nologo",
                        "-nocolor",
                        "-noshadow",
                        "-parallel", "none",
                        "-appdomains", "denied",
                        "-verbose",
                        "-xml", outputPath,
                    },
                ]) ?? 2);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
            }
            Quit(exitCode);
        }

        private static void WaitForUnity()
        {
            for (var remaining = 3000; remaining > 0; remaining--)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == "Assembly-CSharp")
                    {
                        return;
                    }
                }
                Thread.Sleep(10);
            }

            throw new TimeoutException("Unity did not finish loading managed assemblies.");
        }

        private static void Quit(int exitCode)
        {
            Environment.ExitCode = exitCode;
            var application = Type.GetType("UnityEngine.Application, UnityEngine.CoreModule", throwOnError: true);
            var quit = application.GetMethod("Quit", [typeof(int)])
                ?? throw new MissingMethodException(application.FullName, "Quit");
            quit.Invoke(null, [exitCode]);
        }
    }
}
