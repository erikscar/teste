using Macro.Services;
static PreviewWindow Window(int id, string process, string title) => new((nint)id, id, process, DateTime.Now.AddMinutes(id), title) { WindowTitle = title };
var one = Window(1,"Mir4G","Mir4G[1]");
var two = Window(2,"Mir4G","Mir4G[2]");
var steam = Window(3,"Mir4S","MIR4[0]");
void Check(bool value, string name) { if(!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
var single = LauncherWindowResolver.Resolve(new[] { two, steam });
Check(!single.ContainsKey("MIR4 Launcher 1") && single["MIR4 Launcher 2"] == two && single["MIR4 Steam"] == steam, "Only launcher 2 and Steam");
var reversed = LauncherWindowResolver.Resolve(new[] {two, one, steam});
Check(reversed["MIR4 Launcher 1"] == one && reversed["MIR4 Launcher 2"] == two, "Opening order does not change identity");
Check(LauncherWindowResolver.Resolve(new[] {one}).Count == 1, "Only launcher 1");
Check(LauncherWindowResolver.Resolve(new[] {Window(4,"Mir4G","MIR4")}).Count == 0, "Unknown title is not assigned by order");
try { LauncherWindowResolver.Resolve(new[] {two,two}); throw new Exception("Duplicate accepted"); } catch(InvalidOperationException) { Console.WriteLine("PASS Duplicate rejected"); }
foreach(var entry in LauncherWindowResolver.Resolve(new WindowPreviewService().ListWindows())) Console.WriteLine($"LIVE {entry.Key}: {entry.Value.WindowTitle} (PID {entry.Value.ProcessId})");
