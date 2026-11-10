using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Runtime.Loader;
class Dump {
static void Main(string[] args) {
var dir=Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (ctx,n) => { var p=Path.Combine(dir,n.Name+".dll"); return File.Exists(p)?ctx.LoadFromAssemblyPath(p):null; };
var a=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir,"HybridCPU.Compiler.Tests.dll"));
var t=a.GetType("HybridCPU_ISE.Tests.CompilerTests.CompilerPhase02GoldenArtifactHarnessTests",true);
var h=a.GetType("HybridCPU_ISE.Tests.TestHelpers.CompilerGoldenArtifactHarness",true);
const BindingFlags flags=BindingFlags.Static|BindingFlags.NonPublic;
var snap=h.GetMethod("Snapshot",flags);
var list= (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(snap.ReturnType));
foreach(var s in (IEnumerable)t.GetField("PositiveSpecs",flags).GetValue(null)) list.Add(snap.Invoke(null,new[]{t.GetMethod("Project",flags).Invoke(null,new[]{s}),s}));
var m=Activator.CreateInstance(h.GetMethod("Render",flags).GetParameters()[0].ParameterType);
m.GetType().GetProperty("SchemaVersion").SetValue(m,1);
m.GetType().GetProperty("Entries").SetValue(m,list);
File.WriteAllText(args[1],(string)h.GetMethod("Render",flags).Invoke(null,new[]{m})+Environment.NewLine);
}}
