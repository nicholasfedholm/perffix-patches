using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// Loads a (patched) mod DLL with the game's assemblies, forces JIT of the given methods (invalid IL -> InvalidProgramException),
// then runs the OverhaulLib AnimatorPlayerMap behaviour test when asked.
internal static class Program
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main(string[] args)
    {
        string dll = args[0];
        string vs = (System.Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("APPDATA"), "Vintagestory"));
        string[] dirs = { vs, Path.Combine(vs, "Lib"), Path.Combine(vs, "Mods"), Path.GetDirectoryName(dll) };
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (string d in dirs) { string p = Path.Combine(d, name.Name + ".dll"); if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p); }
            return null;
        };
        Assembly asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        foreach (string spec in args.Skip(1).Where(a => a.Contains("::")))
        {
            string[] parts = spec.Split("::");
            Type t = asm.GetType(parts[0], true);
            foreach (MethodBase m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                         .Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                         .Where(m => parts[1] == "*" || m.Name == parts[1]))
            {
                if (m.IsAbstract || m.ContainsGenericParameters) continue;
                try { RuntimeHelpers.PrepareMethod(m.MethodHandle); Check(true, "JIT " + t.Name + "." + m.Name); }
                catch (Exception e) { Check(false, "JIT " + t.Name + "." + m.Name + ": " + e.GetType().Name + " " + e.Message); }
            }
        }
        if (args.Contains("--all")) { int n = 0, bad = 0; Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); Console.WriteLine("type load issues: " + e.LoaderExceptions.Length); }
            foreach (Type t in types) foreach (MethodBase m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))) {
                if (m.IsAbstract || m.ContainsGenericParameters || t.ContainsGenericParameters || m.GetMethodBody() == null) continue; n++;
                try { RuntimeHelpers.PrepareMethod(m.MethodHandle); } catch (Exception e) { bad++; Console.WriteLine("JITFAIL " + t.FullName + "." + m.Name + " " + e.GetType().Name); } }
            Console.WriteLine($"JIT all: {n} methods, {bad} failed"); if (bad > 0) failures++; }
        if (args.Contains("--olmap")) OverhaulMapTest(asm);
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    static void OverhaulMapTest(Assembly asm)
    {
        Type mapT = asm.GetType("CombatOverhaul.Integration.AnimationPatches+AnimatorPlayerMap", true);
        Assembly api = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "VintagestoryAPI");
        Type animT = api.GetType("Vintagestory.API.Common.ClientAnimator", true);
        Type playerT = api.GetType("Vintagestory.API.Common.EntityPlayer", true);
        FieldInfo idF = api.GetType("Vintagestory.API.Common.Entities.Entity").GetField("EntityId");
        object map = Activator.CreateInstance(mapT);
        MethodInfo add = mapT.GetMethod("Add"), get = mapT.GetMethod("Get"), clear = mapT.GetMethod("Clear");
        object a1 = RuntimeHelpers.GetUninitializedObject(animT), a2 = RuntimeHelpers.GetUninitializedObject(animT);
        object p1 = RuntimeHelpers.GetUninitializedObject(playerT), p2 = RuntimeHelpers.GetUninitializedObject(playerT);
        idF.SetValue(p1, 1L); idF.SetValue(p2, 2L);
        (bool, object) G(object a) { var ar = new object[] { a, null }; bool r = (bool)get.Invoke(map, ar); return (r, ar[1]); }

        var r = G(a1); Check(!r.Item1 && r.Item2 == null, "unknown animator -> false");
        r = G(a1); Check(!r.Item1 && r.Item2 == null, "unknown animator (cached) -> false");
        add.Invoke(map, new[] { a1, p1 });
        r = G(a1); Check(r.Item1 && r.Item2 == p1, "after Add, cached miss is invalidated -> p1");
        r = G(a1); Check(r.Item1 && r.Item2 == p1, "cached hit -> p1");
        r = G(a2); Check(!r.Item1, "other animator -> false");
        r = G(a1); Check(r.Item1 && r.Item2 == p1, "back to a1 -> p1");
        add.Invoke(map, new[] { a1, p2 });
        r = G(a1); Check(r.Item1 && r.Item2 == p2, "after re-Add with other player -> p2");
        clear.Invoke(map, null);
        r = G(a1); Check(!r.Item1 && r.Item2 == null, "after Clear -> false");
        add.Invoke(map, new[] { a2, p1 });
        object map2 = Activator.CreateInstance(mapT);
        var ar2 = new object[] { a2, null }; bool r2 = (bool)get.Invoke(map2, ar2);
        Check(!r2, "new map instance does not see old cache");
        bool threw = false; try { G(null); } catch (TargetInvocationException e) when (e.InnerException is ArgumentNullException) { threw = true; }
        Check(threw, "null animator still throws ArgumentNullException like the original");
    }
}
