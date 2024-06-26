﻿using System.Reflection;
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
string Source = args[0];
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
string Target = args[1];
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
int Created = 0;
int Changed = 0;
int Deleted = 0;
int Failing = 0;
List<string> FailedPaths = [];

//start status task
var statusTask = Task.Run(ShowStatus);

//run backup
Backup(Source, Target, State);

//save new state
File.WriteAllText(Target + "/BackupState.bin", State.Encode());

//wait for status task to finish
Running = false;
await statusTask;

//done
Console.WriteLine("Done!");



void Backup(string source, string target, StateTree state)
{
    //remove deleted directories
    foreach (var kv in state.Directories)
        if (!Directory.Exists(source + '/' + kv.Key))
            switch (DeleteAndCount(target + '/' + kv.Key, state))
            {
                case DirectoryDeletionResult.Success:
                    state.Directories.Remove(kv.Key);
                    break;
                case DirectoryDeletionResult.AllFailed:
                    Failing++;
                    FailedPaths.Add(target + '/' + kv.Key);
                    break;
            }

    //remove deleted files
    foreach (var kv in state.Files)
        if (!File.Exists(source + '/' + kv.Key))
        {
            File.Delete(target + '/' + kv.Key);
            state.Directories.Remove(kv.Key);
        }

    DirectoryInfo sourceInfo = new(source);

    //add/update directories
    foreach (var directory in sourceInfo.GetDirectories().Select(x => x.Name))
    {
        if (!state.Directories.TryGetValue(directory, out var subState))
        {
            subState = new();
            state.Directories[directory] = subState;
            Directory.CreateDirectory(target + '/' + directory);
        }

        Backup(source + '/' + directory, target + '/' + directory, subState);
    }

    //add/update files
    foreach (var file in sourceInfo.GetFiles().Select(x => x.Name))
    {
        string timestamp = File.GetLastWriteTimeUtc(source + '/' + file).Ticks.ToString();
        if ((!state.Files.TryGetValue(file, out var savedTimestamp)) || savedTimestamp != timestamp)
        {
            state.Files[file] = timestamp;
            File.Copy(source + '/' + file, target + '/' + file, true);
        }
    }
}

void ShowStatus()
{
    //remember top offset
    int topOffset = Console.CursorTop;

    //initial write
    Console.WriteLine("Created: 0");
    Console.WriteLine("Changed: 0");
    Console.WriteLine("Deleted: 0");
    Console.WriteLine("Failing: 0");

    while (Running)
    {
        Thread.Sleep(100);
        Console.CursorLeft = 9;
        Console.CursorTop = topOffset;
        Console.Write(Created);

        Console.CursorLeft = 9;
        Console.CursorTop = topOffset + 1;
        Console.Write(Changed);

        Console.CursorLeft = 9;
        Console.CursorTop = topOffset + 2;
        Console.Write(Deleted);

        Console.CursorLeft = 9;
        Console.CursorTop = topOffset + 3;
        Console.Write(Failing);
    }
}

DirectoryDeletionResult DeleteAndCount(string path, StateTree tree)
{
    bool anySucceeded = false;
    List<string> failed = [];

    foreach (var kv in tree.Directories)
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
                failed.Add(path[(Target.Length+1)..] + '/' + kv.Key);
                break;
        }
    
    foreach (var kv in tree.Files)
        try
        {
            File.Delete(path + '/' + kv.Key);
            tree.Files.Remove(kv.Key);
            Deleted++;
            anySucceeded = true;
        }
        catch
        {
            failed.Add(path[(Target.Length+1)..] + '/' + kv.Key);
        }
    
    if (failed.Count == 0)
        try
        {
            Directory.Delete(path);
            Deleted++;
            return DirectoryDeletionResult.Success;
        }
        catch
        {
            return DirectoryDeletionResult.AllFailed;
        }
    else if (anySucceeded)
    {
        FailedPaths.AddRange(failed);
        Failing += failed.Count;
        return DirectoryDeletionResult.SomeFailed;
    }
    else
    {
        return DirectoryDeletionResult.AllFailed;
    }
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
                .. Directories.Select(x => $"{x.Key}=({x.Value.Encode()})"),
                .. Files.Select(x => $"{x.Key}={x.Value}")
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
            string key = keyBuilder.ToString();

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