using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tracker.Tests;

/// <summary>
/// Proof for hard rule 2 (and rule 5): the tracker and everything it references — its own
/// source, the vendored SoulMemory subset and the shared MapCore library — declare none of the
/// banned process APIs, open the game with the read + limited-query mask only, and reference
/// no networking. Checked twice: on the source text and on the compiled assemblies.
/// </summary>
[TestClass]
public class ReadOnlyGuaranteeTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>The only native functions the tracker may import. Each one queries or reads.</summary>
    private static readonly string[] AllowedImports =
    {
        "kernel32.dll!OpenProcess",
        "kernel32.dll!ReadProcessMemory",
        "kernel32.dll!CloseHandle",
        "kernel32.dll!GetExitCodeProcess",
        "kernel32.dll!QueryFullProcessImageNameW",
        "ntdll.dll!NtQueryInformationProcess",
        "advapi32.dll!OpenProcessToken",
        "advapi32.dll!GetTokenInformation",
        // Phase 3, read-only queries about the game window (position, size, minimized, foreground):
        "user32.dll!FindWindowExW",
        "user32.dll!GetWindowThreadProcessId",
        "user32.dll!IsWindowVisible",
        "user32.dll!IsIconic",
        "user32.dll!GetClientRect",
        "user32.dll!ClientToScreen",
        "user32.dll!GetWindowRect",
        "user32.dll!GetForegroundWindow",
        // Phase 3, styling and placing the tracker's own overlay window:
        "user32.dll!GetWindowLongPtrW",
        "user32.dll!SetWindowLongPtrW",
        "user32.dll!SetWindowPos",
        // Phase 3, global hotkeys delivered to the tracker's own window:
        "user32.dll!RegisterHotKey",
        "user32.dll!UnregisterHotKey",
        // Phase 3, reading controller state:
        "xinput1_4.dll!XInputGetState",
    };

    /// <summary>
    /// Banned by name in hard rule 2, plus their close relatives: writing or allocating memory in
    /// another process, changing page protection, starting threads there, loading DLLs, hooks,
    /// debugging, suspending — and anything that sends input to, focuses, starts or stops a program.
    /// </summary>
    private static readonly string[] BannedNames =
    {
        "WriteProcessMemory", "VirtualAllocEx", "VirtualProtectEx", "CreateRemoteThread",
        "NtWriteVirtualMemory", "NtAllocateVirtualMemory", "NtProtectVirtualMemory", "VirtualFreeEx",
        "CreateRemoteThreadEx", "NtCreateThreadEx", "RtlCreateUserThread", "QueueUserAPC",
        "LoadLibrary", "GetProcAddress", "SetWindowsHookEx", "SetWinEventHook",
        "DebugActiveProcess", "NtSuspendProcess", "NtResumeProcess", "SuspendThread", "SetThreadContext",
        "TerminateProcess", "CreateProcess", "ShellExecute",
        "SendInput", "keybd_event", "mouse_event", "PostMessage", "SendMessage",
        "SetForegroundWindow", "BringWindowToTop", "SetFocus", "ShowWindow",
        "PROCESS_ALL_ACCESS", "PROCESS_VM_WRITE", "PROCESS_VM_OPERATION", "PROCESS_CREATE_THREAD",
    };

    /// <summary>Managed members that would start, stop or over-open a process, or talk to the network.</summary>
    private static readonly string[] BannedManagedMembers =
    {
        "System.Diagnostics.Process::Start", "System.Diagnostics.Process::Kill", "System.Diagnostics.Process::CloseMainWindow",
        "System.Diagnostics.Process::get_Handle", "System.Diagnostics.Process::get_SafeHandle",
        "System.Diagnostics.Process::get_MainModule", "System.Diagnostics.Process::get_Modules",
        "System.Diagnostics.Process::get_MainWindowHandle", "System.Diagnostics.Process::EnterDebugMode",
    };

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "LootGoblinsMap.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (LootGoblinsMap.slnx) not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Every source file that ends up in the tracker: its own project and the shared library it references.</summary>
    private static List<string> ShippedSourceFiles()
    {
        string root = RepoRoot();
        var files = new List<string>();
        foreach (string project in new[] { "src/Tracker", "src/MapCore" })
        {
            string dir = Path.Combine(root, project);
            files.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f =>
            {
                string rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                return !rel.StartsWith("bin/") && !rel.StartsWith("obj/") && !rel.Contains("/bin/") && !rel.Contains("/obj/");
            }));
        }
        return files;
    }

    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(code, @"//[^\n]*", " ");
    }

    // ---- source ---------------------------------------------------------------------------

    [TestMethod]
    public void Source_ContainsNoBannedApiName_Anywhere()
    {
        var files = ShippedSourceFiles();
        Assert.IsTrue(files.Count(f => f.EndsWith(".cs")) >= 20, "expected to find the tracker's source files");

        var hits = new List<string>();
        foreach (string file in files)
        {
            // Whole text, comments and docs included: the names must not appear at all.
            string text = File.ReadAllText(file);
            foreach (string banned in BannedNames)
                if (text.Contains(banned, StringComparison.OrdinalIgnoreCase))
                    hits.Add($"{Path.GetRelativePath(RepoRoot(), file)}: {banned}");
        }

        TestContext.WriteLine($"scanned {files.Count} files under src/Tracker and src/MapCore for {BannedNames.Length} banned names: {hits.Count} hits");
        Assert.AreEqual(0, hits.Count, "banned API names found:\n" + string.Join("\n", hits));
    }

    [TestMethod]
    public void Source_DeclaresOnlyAllowlistedNativeImports()
    {
        var declared = new List<string>();
        var importFiles = new HashSet<string>();
        var import = new Regex(@"\[(?:DllImport|LibraryImport)\(\s*""(?<dll>[^""]+)""[^\]]*\]\s*(?:\[[^\]]*\]\s*)*[^;{]*?\bextern\b[^;(]*?\b(?<name>\w+)\s*\(", RegexOptions.Singleline);

        foreach (string file in ShippedSourceFiles().Where(f => f.EndsWith(".cs")))
        {
            string code = StripComments(File.ReadAllText(file));
            foreach (Match m in import.Matches(code))
            {
                declared.Add($"{m.Groups["dll"].Value.ToLowerInvariant()}!{m.Groups["name"].Value}");
                importFiles.Add(Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/'));
            }
            // Nothing may sneak in through function pointers or runtime-loaded libraries either.
            foreach (string loader in new[] { "NativeLibrary", "GetDelegateForFunctionPointer", "delegate* unmanaged", "UnmanagedCallersOnly" })
                Assert.IsFalse(code.Contains(loader), $"{file} uses {loader}");
            // Each attribute must have been matched to a declaration above.
            Assert.AreEqual(Regex.Matches(code, @"\[(?:DllImport|LibraryImport)\(").Count, import.Matches(code).Count,
                $"{file}: a native import was not understood by this scan");
        }

        TestContext.WriteLine("native imports declared in source: " + string.Join(", ", declared));
        CollectionAssert.AreEquivalent(AllowedImports, declared.Distinct().ToArray());
        CollectionAssert.AreEqual(new[] { "src/Tracker/Memory/NativeMethods.cs" }, importFiles.ToArray(),
            "all native imports live in one file");
    }

    [TestMethod]
    public void Source_OpensTheGameOnlyWithReadAndLimitedQueryRights()
    {
        string path = Path.Combine(RepoRoot(), "src", "Tracker", "Memory", "NativeMethods.cs");
        string code = StripComments(File.ReadAllText(path));

        // The masks themselves.
        Assert.AreEqual(0x0010u, Memory.NativeMethods.ProcessVmRead);
        Assert.AreEqual(0x1000u, Memory.NativeMethods.ProcessQueryLimitedInformation);
        Assert.AreEqual(0x1010u, Memory.NativeMethods.ReadOnlyAccess, "PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION and nothing more");
        Assert.AreEqual(0x1000u, Memory.NativeMethods.QueryOnlyAccess, "a strict subset of the read-only mask");

        // The open call is private, and called exactly twice, each time with one of those two constants.
        StringAssert.Contains(code, "private static extern nint OpenProcess(");
        var calls = Regex.Matches(code, @"(?<!extern nint )\bOpenProcess\((?<arg>[^,]+),");
        CollectionAssert.AreEquivalent(new[] { "ReadOnlyAccess", "QueryOnlyAccess" }, calls.Select(c => c.Groups["arg"].Value.Trim()).ToArray());

        // And no other file can reach it.
        foreach (string file in ShippedSourceFiles().Where(f => f.EndsWith(".cs") && !f.EndsWith("NativeMethods.cs")))
            Assert.IsFalse(Regex.IsMatch(StripComments(File.ReadAllText(file)), @"\bOpenProcess\s*\("), $"{file} calls the open function directly");
    }

    // ---- compiled assemblies --------------------------------------------------------------

    private sealed record AssemblyFacts(string Name, List<string> Imports, List<string> References, List<string> MemberReferences, List<string> Strings);

    private static AssemblyFacts Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var imports = new List<string>();
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            var import = method.GetImport();
            if (import.Module.IsNil)
                continue;
            string module = md.GetString(md.GetModuleReference(import.Module).Name).ToLowerInvariant();
            imports.Add($"{module}!{md.GetString(import.Name)}");
        }

        var references = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();

        var members = new List<string>();
        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);
            string parent = member.Parent.Kind switch
            {
                HandleKind.TypeReference => TypeName(md, (TypeReferenceHandle)member.Parent),
                HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)member.Parent).Name),
                _ => "?",
            };
            members.Add($"{parent}::{md.GetString(member.Name)}");
        }
        foreach (var handle in md.TypeReferences)
            members.Add(TypeName(md, handle) + "::");

        var strings = new List<string>();
        for (var h = MetadataTokens.UserStringHandle(1); !h.IsNil; h = md.GetNextHandle(h))
            strings.Add(md.GetUserString(h));

        return new AssemblyFacts(Path.GetFileName(path), imports, references, members, strings);
    }

    private static string TypeName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        string ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? md.GetString(type.Name) : ns + "." + md.GetString(type.Name);
    }

    /// <summary>Tracker.dll plus every non-framework assembly it references (that is MapCore.dll).</summary>
    private static List<AssemblyFacts> ShippedAssemblies()
    {
        string trackerPath = typeof(Tracker.Program).Assembly.Location;
        string dir = Path.GetDirectoryName(trackerPath)!;
        var result = new List<AssemblyFacts>();
        var pending = new Queue<string>(new[] { trackerPath });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            string path = pending.Dequeue();
            if (!seen.Add(path))
                continue;
            var facts = Inspect(path);
            result.Add(facts);
            foreach (string reference in facts.References)
            {
                // Framework assemblies (System.*, Presentation*, WindowsBase, …) are Microsoft's; anything
                // else that sits next to Tracker.dll is ours or vendored and gets inspected too.
                string local = Path.Combine(dir, reference + ".dll");
                if (File.Exists(local) && !IsFramework(reference))
                    pending.Enqueue(local);
            }
        }
        return result;
    }

    private static bool IsFramework(string name) =>
        name is "mscorlib" or "netstandard" or "WindowsBase" or "PresentationCore" or "PresentationFramework" or "System" ||
        name.StartsWith("System.") || name.StartsWith("Microsoft.");

    [TestMethod]
    public void Compiled_TrackerAndItsReferences_ImportOnlyAllowlistedNatives()
    {
        var assemblies = ShippedAssemblies();
        CollectionAssert.AreEquivalent(new[] { "Tracker.dll", "MapCore.dll" }, assemblies.Select(a => a.Name).ToArray(),
            "the tracker ships exactly its own assembly and the shared library");

        foreach (var a in assemblies)
        {
            TestContext.WriteLine($"{a.Name}: {a.Imports.Count} native imports [{string.Join(", ", a.Imports)}]");
            foreach (string import in a.Imports)
                CollectionAssert.Contains(AllowedImports, import, $"{a.Name} imports {import}");
        }
        CollectionAssert.AreEquivalent(AllowedImports, assemblies.Single(a => a.Name == "Tracker.dll").Imports);
        Assert.AreEqual(0, assemblies.Single(a => a.Name == "MapCore.dll").Imports.Count, "the shared library has no native code at all");
    }

    [TestMethod]
    public void Compiled_TrackerAndItsReferences_ContainNoBannedNames()
    {
        foreach (var a in ShippedAssemblies())
        {
            var haystack = a.Imports.Concat(a.MemberReferences).Concat(a.Strings).ToList();
            foreach (string banned in BannedNames)
            {
                // "ShowWindow"/"SetFocus"/"SendMessage" as raw Win32 names; WPF's own managed members are not these.
                var hits = haystack.Where(s => Regex.IsMatch(s, $@"(^|[^A-Za-z]){Regex.Escape(banned)}([^a-z]|$)")).ToList();
                Assert.AreEqual(0, hits.Count, $"{a.Name} mentions {banned}: {string.Join("; ", hits)}");
            }
            foreach (string member in BannedManagedMembers)
                Assert.IsFalse(a.MemberReferences.Contains(member), $"{a.Name} references {member}");
        }
    }

    [TestMethod]
    public void Compiled_TrackerAndItsReferences_HaveNoNetworking()
    {
        foreach (var a in ShippedAssemblies())
        {
            TestContext.WriteLine($"{a.Name} references: {string.Join(", ", a.References.OrderBy(r => r))}");
            foreach (string reference in a.References)
                Assert.IsFalse(reference.StartsWith("System.Net", StringComparison.Ordinal) || reference.Contains("Http") || reference.Contains("Sockets"),
                    $"{a.Name} references the networking assembly {reference}");
            foreach (string member in a.MemberReferences)
                Assert.IsFalse(member.StartsWith("System.Net.", StringComparison.Ordinal), $"{a.Name} uses the networking type {member}");
        }
    }
}
