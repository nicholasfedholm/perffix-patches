using Mono.Cecil;
// usage: Inspect <dll> <typeFullName|*field:TypeName> ...   prints member signatures only (no IL)
var asm = AssemblyDefinition.ReadAssembly(args[0]);
var all = asm.MainModule.GetTypes().ToList();
foreach (var q in args.Skip(1)) {
  if (q.StartsWith("*field:")) {
    var tn = q[7..];
    foreach (var t in all) foreach (var f in t.Fields) if (f.FieldType.FullName.Contains(tn)) Console.WriteLine($"FIELD-REF {t.FullName}::{f.Name} : {f.FieldType.FullName} {(f.IsPublic?"public":"nonpublic")}{(f.IsStatic?" static":"")}");
    foreach (var t in all) foreach (var p in t.Properties) if (p.PropertyType.FullName.Contains(tn)) Console.WriteLine($"PROP-REF {t.FullName}::{p.Name} : {p.PropertyType.FullName} get={(p.GetMethod?.IsPublic)}");
    continue;
  }
  var ty = all.FirstOrDefault(t => t.FullName == q);
  if (ty == null) { Console.WriteLine($"?? {q} not found"); continue; }
  Console.WriteLine($"TYPE {ty.FullName} : {ty.BaseType?.FullName} [{string.Join(",", ty.Interfaces.Select(i=>i.InterfaceType.Name))}] {(ty.IsPublic||ty.IsNestedPublic?"public":"nonpublic")}{(ty.IsSealed?" sealed":"")}");
  foreach (var f in ty.Fields) Console.WriteLine($"  F {(f.IsPublic?"pub":f.IsAssembly?"int":"priv")}{(f.IsStatic?" static":"")}{(f.IsInitOnly?" ro":"")} {f.FieldType.FullName} {f.Name}");
  foreach (var p in ty.Properties) Console.WriteLine($"  P {p.PropertyType.FullName} {p.Name} get={(p.GetMethod==null?"-":p.GetMethod.IsPublic?"pub":"np")} set={(p.SetMethod==null?"-":p.SetMethod.IsPublic?"pub":"np")}");
  foreach (var e in ty.Events) Console.WriteLine($"  E {e.EventType.FullName} {e.Name}");
  foreach (var m in ty.Methods) Console.WriteLine($"  M {(m.IsPublic?"pub":m.IsAssembly?"int":"priv")}{(m.IsStatic?" static":"")}{(m.IsVirtual?" virt":"")} {m.ReturnType.FullName} {m.Name}({string.Join(", ", m.Parameters.Select(p=>p.ParameterType.FullName+" "+p.Name))}) il={m.Body?.Instructions.Count}");
  foreach (var n in ty.NestedTypes) Console.WriteLine($"  N {n.Name} methods=[{string.Join(",", n.Methods.Select(x=>x.Name))}] fields=[{string.Join(",", n.Fields.Select(x=>x.FieldType.Name+" "+x.Name))}]");
}
