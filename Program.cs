using System.Reflection;
using System.Text;

Console.WriteLine($"uwap.org/backto {VersionString(Assembly.GetExecutingAssembly())}");

//check arguments
if (args.Length != 2)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Invalid parameters!");
    Console.ForegroundColor = ConsoleColor.Blue;
    Console.Write("Usage:");
    Console.ResetColor();
    Console.WriteLine(" backto [source] [target]");
    return;
}
string Source = Path.GetFullPath(args[0]).TrimEnd('/', '\\');
if (!Directory.Exists(Source))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Invalid source!");
    Console.ForegroundColor = ConsoleColor.Blue;
    Console.Write("Usage:");
    Console.ResetColor();
    Console.WriteLine(" backto [source] [target]");
    return;
}
if (File.Exists(Source + "/BackupState.bin"))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("The source contains BackupState.bin, meaning it's probably a backup target!");
    Console.ForegroundColor = ConsoleColor.Blue;
    Console.Write("Usage:");
    Console.ResetColor();
    Console.WriteLine(" backto [source] [target]");
    return;
}
string Target = Path.GetFullPath(args[1]).TrimEnd('/', '\\');
if (!Directory.Exists(Target))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Invalid target!");
    Console.ForegroundColor = ConsoleColor.Blue;
    Console.Write("Usage:");
    Console.ResetColor();
    Console.WriteLine(" backto [source] [target]");
    return;
}

StateTree State;
if (File.Exists(Target + "/BackupState.bin"))
{
    //target contains BackupState.bin, so the backup will be updated
    State = StateTree.Load(Target + "/BackupState.bin");
}
else
{
    if (Directory.GetFiles(Target).Length > 0 || Directory.GetDirectories(Target).Length > 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("The target doesn't contain BackupState.bin but isn't empty!");
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.Write("Usage:");
        Console.ResetColor();
        Console.WriteLine(" backto [source] [target]");
        return;
    }

    //target doesn't contain BackupState.bin, so a fresh backup will be created
    State = new();
}

//status variables
bool Running = true;

string Current = "";
int Checked = 0;
int Created = 0;
int Changed = 0;
int Deleted = 0;
int Failing = 0;
List<string> FailedPaths = [];

//start status task
var statusTask = Task.Run(ShowStatus);

//run backup
if (Backup(Source, Target, State) == DirectoryBackupResult.AllFailed)
    FailedPaths.Add("/");

//save new state
File.WriteAllText(Target + "/BackupState.bin", State.Encode());

//wait for status task to finish
Running = false;
Current = "Finishing...";
await statusTask;

//done
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("Done!");
Console.ResetColor();

//print failures
if (FailedPaths.Count != 0)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Failed:");
    foreach (var path in FailedPaths)
        Console.WriteLine(path);
    Console.ResetColor();
}


DirectoryBackupResult Backup(string source, string target, StateTree state)
{
    bool anySucceeded = false;
    List<string> failed = [];

    //remove deleted directories
    foreach (var kv in state.Directories)
    {
        SetCurrentPath(source + '/' + kv.Key);
        if (Directory.Exists(source + '/' + kv.Key))
            anySucceeded = true;
        else if (new DirectoryInfo(target + '/' + kv.Key).LinkTarget == null)
            switch (DeleteAndCount(target + '/' + kv.Key, kv.Value))
            {
                case DirectoryDeletionResult.Success:
                    anySucceeded = true;
                    state.Directories.Remove(kv.Key);
                    break;
                case DirectoryDeletionResult.SomeFailed:
                    anySucceeded = true;
                    break;
                case DirectoryDeletionResult.AllFailed:
                    failed.Add(target[Target.Length..] + '/' + kv.Key);
                    break;
            }
        else
        {
            Deleted++;
            Directory.Delete(target + '/' + kv.Key);
            state.Directories.Remove(kv.Key);
            anySucceeded = true;
        }
    }

    //remove deleted files
    foreach (var kv in state.Files)
    {
        SetCurrentPath(source + '/' + kv.Key);
        if (!File.Exists(source + '/' + kv.Key))
            try
            {
                Deleted++;
                File.Delete(target + '/' + kv.Key);
                state.Files.Remove(kv.Key);
                anySucceeded = true;
            }
            catch
            {
                failed.Add(target[Target.Length..] + '/' + kv.Key);
            }
        else anySucceeded = true;
    }

    DirectoryInfo sourceInfo = new(source);

    //add/update directories
    foreach (var directoryInfo in sourceInfo.GetDirectories())
    {
        var directory = directoryInfo.Name;
        SetCurrentPath(source + '/' + directory);
        try
        {
            if (directoryInfo.LinkTarget == null)
            {
                if (!state.Directories.TryGetValue(directory, out var subState))
                {
                    Created++;
                    Directory.CreateDirectory(target + '/' + directory);
                    subState = new();
                    state.Directories[directory] = subState;
                }
                else if (new DirectoryInfo(target + '/' + directory).LinkTarget != null)
                {
                    Changed++;
                    Directory.Delete(target + '/' + directory);
                    Directory.CreateDirectory(target + '/' + directory);
                    subState = new();
                    state.Directories[directory] = subState;
                }
                
                anySucceeded = true;

                switch (Backup(source + '/' + directory, target + '/' + directory, subState))
                {
                    case DirectoryBackupResult.Success:
                    case DirectoryBackupResult.SomeFailed:
                        anySucceeded = true;
                        break;
                    case DirectoryBackupResult.AllFailed:
                        failed.Add(target[Target.Length..] + '/' + directory);
                        break;
                    //no action for NoAction
                }
            }
            else
            {
                if (!state.Directories.TryGetValue(directory, out var subState))
                {
                    Created++;
                    LinkDirectory(target + '/' + directory, directoryInfo.LinkTarget);
                    state.Directories[directory] = new();
                    anySucceeded = true;
                }
                else
                {
                    var otherTarget = new DirectoryInfo(target + '/' + directory).LinkTarget;
                    if (otherTarget == null)
                    {
                        Changed++;
                        Directory.Delete(target + '/' + directory, true);
                        LinkDirectory(target + '/' + directory, directoryInfo.LinkTarget);
                        state.Directories[directory] = new();
                        anySucceeded = true;
                    }
                    else if (otherTarget != directoryInfo.LinkTarget)
                    {
                        Changed++;
                        LinkDirectory(target + '/' + directory, directoryInfo.LinkTarget);
                        anySucceeded = true;
                    }
                }
            }
        }
        catch
        {
            failed.Add(target[Target.Length..] + '/' + directory);
        }
    }

    //add/update files
    foreach (var fileInfo in sourceInfo.GetFiles())
    {
        var file = fileInfo.Name;
        SetCurrentPath(source + '/' + file);
        try
        {
            string timestamp = File.GetLastWriteTimeUtc(source + '/' + file).Ticks.ToString();
            if (!state.Files.TryGetValue(file, out var savedTimestamp))
                Created++;
            else if (savedTimestamp != timestamp)
                Changed++;
            else
                continue;
                
            if (fileInfo.LinkTarget == null)
                File.Copy(source + '/' + file, target + '/' + file, true);
            else
                LinkFile(target + '/' + file, fileInfo.LinkTarget);
            
            state.Files[file] = timestamp;
            anySucceeded = true;
        }
        catch
        {
            failed.Add(target[Target.Length..] + '/' + file);
        }
    }
    
    if (failed.Count == 0)
        return anySucceeded ? DirectoryBackupResult.Success : DirectoryBackupResult.NoAction;

    Failing += failed.Count;
    if (anySucceeded)
    {
        FailedPaths.AddRange(failed);
        return DirectoryBackupResult.SomeFailed;
    }
    return DirectoryBackupResult.AllFailed;
}

void SetCurrentPath(string path)
{
    Checked++;
    if (path.Length > Console.BufferWidth - 10)
        Current = "..." + path[(path.Length - Console.BufferWidth + 13)..];
    else Current = path;
}

void ShowStatus()
{
    //initial write
    Console.ForegroundColor = ConsoleColor.Blue;
    Console.WriteLine("Current: ");
    Console.WriteLine("Checked: 0");
    Console.WriteLine("Created: 0");
    Console.WriteLine("Changed: 0");
    Console.WriteLine("Deleted: 0");
    Console.Write("Failing: 0");
    Console.ResetColor();

    bool running = true;
    string lastCurrent = "";

    while (true)
    {
        Thread.Sleep(100);

        Console.CursorLeft = 9;
        Console.CursorTop -= 5;
        string current = Current;
        Console.Write(current);
        for (int i = current.Length; i < lastCurrent.Length; i++)
            Console.Write(' ');
        lastCurrent = current;

        Console.CursorLeft = 9;
        Console.CursorTop++;
        Console.Write(Checked);

        Console.CursorLeft = 9;
        Console.CursorTop++;
        Console.Write(Created);

        Console.CursorLeft = 9;
        Console.CursorTop++;
        Console.Write(Changed);

        Console.CursorLeft = 9;
        Console.CursorTop++;
        Console.Write(Deleted);

        Console.CursorLeft = 9;
        Console.CursorTop++;
        Console.Write(Failing);
        
        if (!running)
            break;
        if (!Running)
            running = false;
    }

    Console.WriteLine();
}

DirectoryDeletionResult DeleteAndCount(string path, StateTree tree)
{
    Deleted++;

    bool anySucceeded = false;
    List<string> failed = [];

    foreach (var kv in tree.Directories)
    {
        SetCurrentPath(path + '/' + kv.Key);
        switch (DeleteAndCount(path + '/' + kv.Key, kv.Value))
        {
            case DirectoryDeletionResult.Success:
                anySucceeded = true;
                tree.Directories.Remove(kv.Key);
                break;
            case DirectoryDeletionResult.SomeFailed:
                anySucceeded = true;
                break;
            case DirectoryDeletionResult.AllFailed:
                failed.Add(path[Target.Length..] + '/' + kv.Key);
                break;
        }
    }
    
    foreach (var kv in tree.Files)
    {
        SetCurrentPath(path + '/' + kv.Key);
        try
        {
            Deleted++;
            File.Delete(path + '/' + kv.Key);
            tree.Files.Remove(kv.Key);
            anySucceeded = true;
        }
        catch
        {
            failed.Add(path[Target.Length..] + '/' + kv.Key);
        }
    }
    
    if (failed.Count == 0)
        try
        {
            Directory.Delete(path, true);
            return DirectoryDeletionResult.Success;
        }
        catch
        {
            return DirectoryDeletionResult.AllFailed;
        }

    Failing += failed.Count;
    if (anySucceeded)
    {
        FailedPaths.AddRange(failed);
        return DirectoryDeletionResult.SomeFailed;
    }
    return DirectoryDeletionResult.AllFailed;
}

static void LinkFile(string path, string linkTarget)
{
    if (File.Exists(path))
        File.Delete(path);
    File.CreateSymbolicLink(path, linkTarget);
}

static void LinkDirectory(string path, string linkTarget)
{
    if (Directory.Exists(path))
        Directory.Delete(path);
    Directory.CreateSymbolicLink(path, linkTarget);
}

static string VersionString(Assembly assembly)
{
    var version = assembly.GetName().Version;
    if (version == null)
        return "0.1";
    if (version.MinorRevision != 0)
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.MinorRevision}";
    if (version.Build != 0)
        return $"{version.Major}.{version.Minor}.{version.Build}";
    return $"{version.Major}.{version.Minor}";
}

public enum DirectoryBackupResult
{
    NoAction,
    Success,
    SomeFailed,
    AllFailed
}

public enum DirectoryDeletionResult
{
    Success,
    SomeFailed,
    AllFailed
}

public class StateTree
{
    public Dictionary<string, StateTree> Directories = [];

    public Dictionary<string, string> Files = [];

    public string Encode()
        => string.Join(';',
            [
                .. Directories.Select(x => $"{x.Key.ToBase64TreeSafe()}=({x.Value.Encode()})"),
                .. Files.Select(x => $"{x.Key.ToBase64TreeSafe()}={x.Value}")
            ]);

    public static StateTree Load(string path)
    {
        using StreamReader reader = new(path);
        StateTree result = new();
        result.LoadRecursive(reader);
        return result;
    }

    private void LoadRecursive(StreamReader reader)
    {
        int read;

        while (true)
        {
            //key
            StringBuilder keyBuilder = new();
            read = reader.Read();
            if ((char)read == ')')
                return;
            while (read != -1 && (char)read != '=')
            {
                keyBuilder.Append((char)read);
                read = reader.Read();
            }
            if (read == -1)
                return;
            string key = keyBuilder.ToString().FromBase64TreeSafe();

            //value
            read = reader.Read();
            switch ((char)read)
            {
                case '(': //directory
                    if (!Directories.TryGetValue(key, out var subTree))
                    {
                        subTree = new();
                        Directories[key] = subTree;
                    }
                    subTree.LoadRecursive(reader);
                    read = reader.Read();
                    break;
                default: //file
                    StringBuilder valueBuilder = new();
                    while (read != -1 && !";)".Contains((char)read))
                    {
                        valueBuilder.Append((char)read);
                        read = reader.Read();
                    }
                    Files[key] = valueBuilder.ToString();
                    break;
            }
            switch (read)
            {
                case -1:
                case ')':
                    return;
                    //the only possible alternative is a ; but nothing needs to be done in that case
            }
        }
    }
}

static class Extensions
{
    public static string ToBase64TreeSafe(this string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).Replace('=', '_');

    public static string FromBase64TreeSafe(this string base64)
        => Encoding.UTF8.GetString(Convert.FromBase64String(base64.Replace('_', '=')));
}